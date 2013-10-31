﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.PropertyEditors.ValueConverters;
using Umbraco.Core.Strings;

namespace Zbu.ModelsBuilder.Umbraco
{
    public class Application : IDisposable
    {
        #region Applicationmanagement

// ReSharper disable once ClassNeverInstantiated.Local
        private class AppHandler : ApplicationEventHandler
        {
            protected override void ApplicationStarting(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
            {
                base.ApplicationStarting(umbracoApplication, applicationContext);

                // remove core converters that are replaced by web converted
                PropertyValueConvertersResolver.Current.RemoveType<TinyMceValueConverter>();
                PropertyValueConvertersResolver.Current.RemoveType<TextStringValueConverter>();
                PropertyValueConvertersResolver.Current.RemoveType<SimpleEditorValueConverter>();
            }
        }

        private bool _installedConfigSystem;
        private static readonly object LockO = new object();
        private static Application _application;
        private global::Umbraco.Web.Standalone.StandaloneApplication _umbracoApplication;

        private Application()
        {
            _standalone = false;
        }

        private Application(string connectionString, string databaseProvider)
        {
            _connectionString = connectionString;
            _databaseProvider = databaseProvider;
            _standalone = true;
        }

        private static string UmbracoVersion
        {
            // this is what ApplicationContext.Configured wants in order to be happy
            get { return global::Umbraco.Core.Configuration.UmbracoVersion.Current.ToString(3); }
        }

        private readonly bool _standalone;
        private readonly string _connectionString;
        private readonly string _databaseProvider;

        public static Application GetApplication()
        {
            lock (LockO)
            {
                if (_application == null)
                {
                    _application = new Application();
                    // do NOT start it!
                }
                return _application;
            }
        }

        public static Application GetApplication(string connectionString, string databaseProvider)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Must not be null nor empty.", "connectionString");
            if (string.IsNullOrWhiteSpace(databaseProvider))
                throw new ArgumentException("Must not be null nor empty.", "databaseProvider");

            lock (LockO)
            {
                if (_application == null)
                {
                    _application = new Application(connectionString, databaseProvider);
                    _application.Start();
                }
                return _application;
            }
        }

        private void Start()
        {
            if (!ConfigSystem.Installed)
            {
                ConfigSystem.Install();
                _installedConfigSystem = true;
            }

            var cstr = new ConnectionStringSettings("umbracoDbDSN", _connectionString, _databaseProvider);
            ConfigurationManager.ConnectionStrings.Add(cstr);
            ConfigurationManager.AppSettings.Add("umbracoConfigurationStatus", UmbracoVersion);

            var app = global::Umbraco.Web.Standalone.StandaloneApplication.GetApplication(Environment.CurrentDirectory)
                .WithoutApplicationEventHandler<global::Umbraco.Web.Search.ExamineEvents>()
                .WithApplicationEventHandler<AppHandler>();

            try
            {
                app.Start(); // will throw if already started
            }
            catch
            {
                if (_installedConfigSystem)
                    ConfigSystem.Uninstall();
                _installedConfigSystem = false;                
                throw;
            }

            _umbracoApplication = app;
        }

        private void Terminate()
        {
            if (_umbracoApplication != null)
            {
                if (_installedConfigSystem)
                {
                    ConfigSystem.Uninstall();
                    _installedConfigSystem = false;
                }

                _umbracoApplication.Terminate();
            }

            lock (LockO)
            {
                _application = null;
            }
        }

        #endregion

        #region Services

        public IList<TypeModel> GetContentAndMediaTypes()
        {
            if (_standalone && _umbracoApplication == null)
                throw new InvalidOperationException("Application is not ready.");

            var contentTypeService = ApplicationContext.Current.Services.ContentTypeService;
            var types = new List<TypeModel>();
            types.AddRange(GetTypes(PublishedItemType.Content, contentTypeService.GetAllContentTypes().Cast<IContentTypeBase>().ToArray()));
            types.AddRange(GetTypes(PublishedItemType.Media, contentTypeService.GetAllMediaTypes().Cast<IContentTypeBase>().ToArray()));
            return types;
        }

        public IList<TypeModel> GetContentTypes()
        {
            if (_standalone && _umbracoApplication == null)
                throw new InvalidOperationException("Application is not ready.");

            var contentTypeService = ApplicationContext.Current.Services.ContentTypeService;
            var contentTypes = contentTypeService.GetAllContentTypes().Cast<IContentTypeBase>().ToArray();
            return GetTypes(PublishedItemType.Content, contentTypes);
        }

        public IList<TypeModel> GetMediaTypes()
        {
            if (_standalone && _umbracoApplication == null)
                throw new InvalidOperationException("Application is not ready.");

            var contentTypeService = ApplicationContext.Current.Services.ContentTypeService;
            var contentTypes = contentTypeService.GetAllMediaTypes().Cast<IContentTypeBase>().ToArray();
            return GetTypes(PublishedItemType.Media, contentTypes);
        }

        private static IList<TypeModel> GetTypes(PublishedItemType itemType, IContentTypeBase[] contentTypes)
        {
            var typeModels = new List<TypeModel>();

            // get the types and the properties
            foreach (var contentType in contentTypes)
            {
                var typeModel = new TypeModel
                {
                    Id = contentType.Id,
                    Alias = contentType.Alias,
                    Name = contentType.Alias.ToCleanString(CleanStringType.PascalCase),
                    BaseTypeId = contentType.ParentId
                };

                typeModels.Add(typeModel);

                var publishedContentType = PublishedContentType.Get(itemType, contentType.Alias);

                foreach (var propertyType in contentType.PropertyTypes)
                {
                    var propertyModel = new PropertyModel
                    {
                        Alias = propertyType.Alias,
                        Name = propertyType.Alias.ToCleanString(CleanStringType.PascalCase)
                    };

                    var publishedPropertyType = publishedContentType.GetPropertyType(propertyType.Alias);
                    propertyModel.ClrType = publishedPropertyType.ClrType;

                    typeModel.Properties.Add(propertyModel);
                }
            }

            // wire the base types
            foreach (var typeModel in typeModels.Where(x => x.BaseTypeId > 0))
            {
                typeModel.BaseType = typeModels.SingleOrDefault(x => x.Id == typeModel.BaseTypeId);
                if (typeModel.BaseType == null) throw new Exception();
            }

            // discover mixins
            foreach (var contentType in contentTypes)
            {
                var typeModel = typeModels.SingleOrDefault(x => x.Id == contentType.Id);
                if (typeModel == null) throw new Exception();

                IEnumerable<IContentTypeComposition> compositionTypes;
                var contentTypeAsMedia = contentType as IMediaType;
                var contentTypeAsContent = contentType as IContentType;
                if (contentTypeAsMedia != null) compositionTypes = contentTypeAsMedia.ContentTypeComposition;
                else if (contentTypeAsContent != null) compositionTypes = contentTypeAsContent.ContentTypeComposition;

                else throw new Exception("Panic: neither a content nor a media type.");

                foreach (var compositionType in compositionTypes)
                {
                    var compositionModel = typeModels.SingleOrDefault(x => x.Id == compositionType.Id);
                    if (compositionModel == null) throw new Exception();

                    if (compositionType.Id != contentType.ParentId)
                    {
                        // add to mixins
                        typeModel.MixinTypes.Add(compositionModel);

                        // mark as mixin - as well as parents
                        compositionModel.IsMixin = true;
                        while ((compositionModel = compositionModel.BaseType) != null)
                            compositionModel.IsMixin = true;
                    }
                }
            }

            return typeModels;
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // managed
                    Terminate();
                }
                // unmanaged
                _disposed = true;
            }
            // base.Dispose()
        }

        //~Application()
        //{
        //    Dispose(false);
        //}

        #endregion
    }
}
