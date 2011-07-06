﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Orchard.Caching;
using Orchard.FileSystems.AppData;
using Orchard.Logging;

namespace Orchard.Environment.Compilation.Dependencies {
    /// <summary>
    /// Similar to "Dependencies.xml" file, except we also store "GetFileHash" result for every 
    /// VirtualPath entry. This is so that if any virtual path reference in the file changes,
    /// the file stored by this component will also change.
    /// </summary>
    public class DefaultExtensionDependenciesManager : IExtensionDependenciesManager {
        private const string BasePath = "Dependencies";
        private const string FileName = "dependencies.compiled.xml";
        private readonly ICacheManager _cacheManager;
        private readonly IAppDataFolder _appDataFolder;
        private readonly InvalidationToken _writeThroughToken;

        public DefaultExtensionDependenciesManager(ICacheManager cacheManager, IAppDataFolder appDataFolder) {
            _cacheManager = cacheManager;
            _appDataFolder = appDataFolder;
            _writeThroughToken = new InvalidationToken();

            Logger = NullLogger.Instance;
        }

        public ILogger Logger { get; set; }

        private string PersistencePath {
            get { return _appDataFolder.Combine(BasePath, FileName); }
        }

        public void StoreDependencies(IEnumerable<DependencyDescriptor> dependencyDescriptors, Func<DependencyDescriptor, string> fileHashProvider) {
            Logger.Information("Storing module dependency file.");

            var newDocument = CreateDocument(dependencyDescriptors, fileHashProvider);
            var previousDocument = ReadDocument(PersistencePath);
            if (XNode.DeepEquals(newDocument.Root, previousDocument.Root)) {
                Logger.Debug("Existing document is identical to new one. Skipping save.");
            }
            else {
                WriteDocument(PersistencePath, newDocument);
            }

            Logger.Information("Done storing module dependency file.");
        }

        public IEnumerable<string> GetVirtualPathDependencies(string extensionId) {
            var descriptor = GetDescriptor(extensionId);
            if (descriptor != null && IsSupportedLoader(descriptor.LoaderName)) {
                // Currently, we return the same file for every module. An improvement would be to return
                // a specific file per module (this would decrease the number of recompilations needed
                // when modules change on disk).
                yield return _appDataFolder.GetVirtualPath(PersistencePath);
            }
        }

        public ActivatedExtensionDescriptor GetDescriptor(string extensionId) {
            return LoadDescriptors().FirstOrDefault(d => d.ExtensionId.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<ActivatedExtensionDescriptor> LoadDescriptors() {
            return _cacheManager.Get(PersistencePath, ctx => {
                Directory.CreateDirectory(_appDataFolder.MapPath(BasePath));
                ctx.Monitor(_appDataFolder.WhenPathChanges(ctx.Key));

                _writeThroughToken.IsCurrent = true;
                ctx.Monitor(_writeThroughToken);

                return ReadDescriptors(ctx.Key).ToList();
            });
        }

        private XDocument CreateDocument(IEnumerable<DependencyDescriptor> dependencies, Func<DependencyDescriptor, string> fileHashProvider) {
            Func<string, XName> ns = (name => XName.Get(name));

            var elements = dependencies
                .Where(dep => IsSupportedLoader(dep.LoaderName))
                .OrderBy(dep => dep.Name, StringComparer.OrdinalIgnoreCase)
                .Select(descriptor =>
                        new XElement(ns("Dependency"),
                            new XElement(ns("ExtensionId"), descriptor.Name),
                            new XElement(ns("LoaderName"), descriptor.LoaderName),
                            new XElement(ns("VirtualPath"), descriptor.VirtualPath),
                            new XElement(ns("Hash"), fileHashProvider(descriptor))));

            return new XDocument(new XElement(ns("Dependencies"), elements.ToArray()));
        }

        private IEnumerable<ActivatedExtensionDescriptor> ReadDescriptors(string persistancePath) {
            Func<string, XName> ns = (name => XName.Get(name));
            Func<XElement, string, string> elem = (e, name) => e.Element(ns(name)).Value;

            XDocument document = ReadDocument(persistancePath);
            return document
                .Elements(ns("Dependencies"))
                .Elements(ns("Dependency"))
                .Select(e => new ActivatedExtensionDescriptor {
                    ExtensionId = elem(e, "ExtensionId"),
                    VirtualPath = elem(e, "VirtualPath"),
                    LoaderName = elem(e, "LoaderName"),
                    Hash = elem(e, "Hash"),
                }).ToList();
        }

        private bool IsSupportedLoader(string loaderName) {
            // Note: this is hard-coded for now, to avoid adding more responsibilities to the IExtensionLoader
            // implementations.
            return
                loaderName == "DynamicExtensionLoader" ||
                loaderName == "PrecompiledExtensionLoader";
        }

        private void WriteDocument(string persistancePath, XDocument document) {
            _writeThroughToken.IsCurrent = false;
            _appDataFolder.StoreFile(persistancePath, document.ToString());
        }

        private XDocument ReadDocument(string persistancePath) {
            if (!File.Exists(_appDataFolder.MapPath(persistancePath))) {
                return new XDocument();
            }

            try {
                return XDocument.Parse(_appDataFolder.ReadFile(persistancePath));
            }
            catch (Exception e) {
                Logger.Information(e, "Error reading file '{0}'. Assuming empty.", persistancePath);
                return new XDocument();
            }
        }

        private class InvalidationToken : IVolatileToken {
            public bool IsCurrent { get; set; }
        }
    }
}