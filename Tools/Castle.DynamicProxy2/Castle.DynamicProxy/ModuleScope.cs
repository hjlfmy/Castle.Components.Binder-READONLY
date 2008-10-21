// Copyright 2004-2008 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;

namespace Castle.DynamicProxy
{
	using System;
	using System.Collections;
	using System.IO;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Resources;
	using System.Threading;
	using Castle.DynamicProxy.Generators;
	using System.Runtime.Serialization.Formatters.Binary;

	/// <summary>
	/// Summary description for ModuleScope.
	/// </summary>
	public class ModuleScope
	{
		/// <summary>
		/// The default file name used when the assembly is saved using <see cref="DEFAULT_FILE_NAME"/>.
		/// </summary>
		public static readonly String DEFAULT_FILE_NAME = "CastleDynProxy2.dll";

		/// <summary>
		/// The default assembly (simple) name used for the assemblies generated by a <see cref="ModuleScope"/> instance.
		/// </summary>
		public static readonly String DEFAULT_ASSEMBLY_NAME = "DynamicProxyGenAssembly2";

		private ModuleBuilder moduleBuilderWithStrongName = null;
		private ModuleBuilder moduleBuilder = null;

		// The names to use for the generated assemblies and the paths (including the names) of their manifest modules
		private readonly string strongAssemblyName;
		private readonly string weakAssemblyName;
		private readonly string strongModulePath;
		private readonly string weakModulePath;

		// Keeps track of generated types
		private readonly Hashtable typeCache = Hashtable.Synchronized(new Hashtable());

		// Users of ModuleScope should use this lock when accessing the cache
		private readonly ReaderWriterLock readerWriterLock = new ReaderWriterLock();

		// Used to lock the module builder creation
		private readonly object _lockobj = new object();

		// Specified whether the generated assemblies are intended to be saved
		private readonly bool savePhysicalAssembly;

		/// <summary>
		/// Initializes a new instance of the <see cref="ModuleScope"/> class; assemblies created by this instance will not be saved.
		/// </summary>
		public ModuleScope() : this(false)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ModuleScope"/> class, allowing to specify whether the assemblies generated by this instance
		/// should be saved.
		/// </summary>
		/// <param name="savePhysicalAssembly">If set to <c>true</c> saves the generated module.</param>
		public ModuleScope(bool savePhysicalAssembly)
			: this(savePhysicalAssembly, DEFAULT_ASSEMBLY_NAME, DEFAULT_FILE_NAME, DEFAULT_ASSEMBLY_NAME, DEFAULT_FILE_NAME)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ModuleScope"/> class, allowing to specify whether the assemblies generated by this instance
		/// should be saved and what simple names are to be assigned to them.
		/// </summary>
		/// <param name="savePhysicalAssembly">If set to <c>true</c> saves the generated module.</param>
		/// <param name="strongAssemblyName">The simple name of the strong-named assembly generated by this <see cref="ModuleScope"/>.</param>
		/// <param name="strongModulePath">The path and file name of the manifest module of the strong-named assembly generated by this <see cref="ModuleScope"/>.</param>
		/// <param name="weakAssemblyName">The simple name of the weak-named assembly generated by this <see cref="ModuleScope"/>.</param>
		/// <param name="weakModulePath">The path and file name of the manifest module of the weak-named assembly generated by this <see cref="ModuleScope"/>.</param>
		public ModuleScope(bool savePhysicalAssembly, string strongAssemblyName, string strongModulePath,
		                   string weakAssemblyName, string weakModulePath)
		{
			this.savePhysicalAssembly = savePhysicalAssembly;
			this.strongAssemblyName = strongAssemblyName;
			this.strongModulePath = strongModulePath;
			this.weakAssemblyName = weakAssemblyName;
			this.weakModulePath = weakModulePath;
		}

		/// <summary>
		/// Users of this <see cref="ModuleScope"/> should use this lock when accessing the cache.
		/// </summary>
		public ReaderWriterLock RWLock
		{
			get { return readerWriterLock; }
		}

		/// <summary>
		/// Returns a type from this scope's type cache, or null if the key cannot be found.
		/// </summary>
		/// <param name="key">The key to be looked up in the cache.</param>
		/// <returns>The type from this scope's type cache matching the key, or null if the key cannot be found</returns>
		public Type GetFromCache(CacheKey key)
		{
			// no lock needed, typeCache is synchronized
			return (Type) typeCache[key];
		}

		/// <summary>
		/// Registers a type in this scope's type cache.
		/// </summary>
		/// <param name="key">The key to be associated with the type.</param>
		/// <param name="type">The type to be stored in the cache.</param>
		public void RegisterInCache(CacheKey key, Type type)
		{
			// no lock needed, typeCache is synchronized
			typeCache[key] = type;
		}

		/// <summary>
		/// Gets the key pair used to sign the strong-named assembly generated by this <see cref="ModuleScope"/>.
		/// </summary>
		/// <returns></returns>
		public static byte[] GetKeyPair()
		{
			byte[] keyPair;

			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Castle.DynamicProxy.DynProxy.snk"))
			{
				if (stream == null)
					throw new MissingManifestResourceException(
						"Should have a Castle.DynamicProxy.DynProxy.snk as an embedded resource, so Dynamic Proxy could sign generated assembly");

				int length = (int) stream.Length;
				keyPair = new byte[length];
				stream.Read(keyPair, 0, length);
			}

			return keyPair;
		}

		/// <summary>
		/// Gets the strong-named module generated by this scope, or <see langword="null"/> if none has yet been generated.
		/// </summary>
		/// <value>The strong-named module generated by this scope, or <see langword="null"/> if none has yet been generated.</value>
		public ModuleBuilder StrongNamedModule
		{
			get
			{
				lock (_lockobj)
				{
					return moduleBuilderWithStrongName;
				}
			}
		}

		/// <summary>
		/// Gets the file name of the strongly named module generated by this scope.
		/// </summary>
		/// <value>The file name of the strongly named module generated by this scope.</value>
		public string StrongNamedModuleName
		{
			get { return Path.GetFileName(strongModulePath); }
		}

		/// <summary>
		/// Gets the directory where the strongly named module generated by this scope will be saved, or <see langword="null"/> if the current directory
		/// is used.
		/// </summary>
		/// <value>The directory where the strongly named module generated by this scope will be saved when <see cref="SaveAssembly()"/> is called
		/// (if this scope was created to save modules).</value>
		public string StrongNamedModuleDirectory
		{
			get
			{
				string directory = Path.GetDirectoryName(strongModulePath);
				if (directory == "")
					return null;
				else
					return directory;
			}
		}

		/// <summary>
		/// Gets the weak-named module generated by this scope, or <see langword="null"/> if none has yet been generated.
		/// </summary>
		/// <value>The weak-named module generated by this scope, or <see langword="null"/> if none has yet been generated.</value>
		public ModuleBuilder WeakNamedModule
		{
			get
			{
				lock (_lockobj)
				{
					return moduleBuilder;
				}
			}
		}

		/// <summary>
		/// Gets the file name of the weakly named module generated by this scope.
		/// </summary>
		/// <value>The file name of the weakly named module generated by this scope.</value>
		public string WeakNamedModuleName
		{
			get { return Path.GetFileName(weakModulePath); }
		}

		/// <summary>
		/// Gets the directory where the weakly named module generated by this scope will be saved, or <see langword="null"/> if the current directory
		/// is used.
		/// </summary>
		/// <value>The directory where the weakly named module generated by this scope will be saved when <see cref="SaveAssembly()"/> is called
		/// (if this scope was created to save modules).</value>
		public string WeakNamedModuleDirectory
		{
			get
			{
				string directory = Path.GetDirectoryName(weakModulePath);
				if (directory == "")
					return null;
				else
					return directory;
			}
		}

		/// <summary>
		/// Gets the specified module generated by this scope, creating a new one if none has yet been generated.
		/// </summary>
		/// <param name="isStrongNamed">If set to true, a strong-named module is returned; otherwise, a weak-named module is returned.</param>
		/// <returns>A strong-named or weak-named module generated by this scope, as specified by the <paramref name="isStrongNamed"/> parameter.</returns>
		public ModuleBuilder ObtainDynamicModule(bool isStrongNamed)
		{
			lock (_lockobj)
			{
				if (isStrongNamed)
					return ObtainDynamicModuleWithStrongName();
				else
					return ObtainDynamicModuleWithWeakName();
			}
		}

		/// <summary>
		/// Gets the strong-named module generated by this scope, creating a new one if none has yet been generated.
		/// </summary>
		/// <returns>A strong-named module generated by this scope.</returns>
		public ModuleBuilder ObtainDynamicModuleWithStrongName()
		{
			lock (_lockobj)
			{
				if (moduleBuilderWithStrongName == null)
				{
					moduleBuilderWithStrongName = CreateModule(true);
				}
				return moduleBuilderWithStrongName;
			}
		}

		/// <summary>
		/// Gets the weak-named module generated by this scope, creating a new one if none has yet been generated.
		/// </summary>
		/// <returns>A weak-named module generated by this scope.</returns>
		public ModuleBuilder ObtainDynamicModuleWithWeakName()
		{
			lock (_lockobj)
			{
				if (moduleBuilder == null)
				{
					moduleBuilder = CreateModule(false);
				}
				return moduleBuilder;
			}
		}

		private ModuleBuilder CreateModule(bool signStrongName)
		{
			AssemblyName assemblyName = GetAssemblyName(signStrongName);

			string moduleName = signStrongName ? StrongNamedModuleName : WeakNamedModuleName;
			string moduleDirectory = signStrongName ? StrongNamedModuleDirectory : WeakNamedModuleDirectory;

			if (savePhysicalAssembly)
			{
				AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
					assemblyName, AssemblyBuilderAccess.RunAndSave, moduleDirectory);

				return assemblyBuilder.DefineDynamicModule(moduleName, moduleName, true);
			}
			else
			{
				AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
					assemblyName,
					AssemblyBuilderAccess.Run);

				return assemblyBuilder.DefineDynamicModule(moduleName, true);
			}
		}

		private AssemblyName GetAssemblyName(bool signStrongName)
		{
			AssemblyName assemblyName = new AssemblyName();
			assemblyName.Name = signStrongName ? strongAssemblyName : weakAssemblyName;

			if (signStrongName)
			{
				byte[] keyPairStream = GetKeyPair();

				if (keyPairStream != null)
				{
					assemblyName.KeyPair = new StrongNameKeyPair(keyPairStream);
				}
			}
			return assemblyName;
		}

		/// <summary>
		/// Saves the generated assembly with the name and directory information given when this <see cref="ModuleScope"/> instance was created (or with
		/// the <see cref="DEFAULT_FILE_NAME"/> and current directory if none was given).
		/// </summary>
		/// <remarks>
		/// <para>
		/// This method stores the generated assembly in the directory passed as part of the module information specified when this instance was
		/// constructed (if any, else the current directory is used). If both a strong-named and a weak-named assembly
		/// have been generated, it will throw an exception; in this case, use the <see cref="SaveAssembly (bool)"/> overload.
		/// </para>
		/// <para>
		/// If this <see cref="ModuleScope"/> was created without indicating that the assembly should be saved, this method does nothing.
		/// </para></remarks>
		/// <exception cref="InvalidOperationException">Both a strong-named and a weak-named assembly have been generated.</exception>
		/// <returns>The path of the generated assembly file, or null if no file has been generated.</returns>
		public string SaveAssembly()
		{
			if (!savePhysicalAssembly)
				return null;

			if (StrongNamedModule != null && WeakNamedModule != null)
				throw new InvalidOperationException("Both a strong-named and a weak-named assembly have been generated.");
			else if (StrongNamedModule != null)
				return SaveAssembly(true);
			else if (WeakNamedModule != null)
				return SaveAssembly(false);
			else
				return null;
		}

		/// <summary>
		/// Saves the specified generated assembly with the name and directory information given when this <see cref="ModuleScope"/> instance was created
		/// (or with the <see cref="DEFAULT_FILE_NAME"/> and current directory if none was given).
		/// </summary>
		/// <param name="strongNamed">True if the generated assembly with a strong name should be saved (see <see cref="StrongNamedModule"/>);
		///   false if the generated assembly without a strong name should be saved (see <see cref="WeakNamedModule"/>.</param>
		/// <remarks>
		/// <para>
		/// This method stores the specified generated assembly in the directory passed as part of the module information specified when this instance was
		/// constructed (if any, else the current directory is used).
		/// </para>
		/// <para>
		/// If this <see cref="ModuleScope"/> was created without indicating that the assembly should be saved, this method does nothing.
		/// </para>
		/// </remarks>
		/// <exception cref="InvalidOperationException">No assembly has been generated that matches the <paramref name="strongNamed"/> parameter.
		/// </exception>
		/// <returns>The path of the generated assembly file, or null if no file has been generated.</returns>
		public string SaveAssembly(bool strongNamed)
		{
			if (!savePhysicalAssembly)
				return null;

			AssemblyBuilder assemblyBuilder;
			string assemblyFileName;
			string assemblyFilePath;

			if (strongNamed)
			{
				if (StrongNamedModule == null)
					throw new InvalidOperationException("No strong-named assembly has been generated.");
				else
				{
					assemblyBuilder = (AssemblyBuilder) StrongNamedModule.Assembly;
					assemblyFileName = StrongNamedModuleName;
					assemblyFilePath = StrongNamedModule.FullyQualifiedName;
				}
			}
			else
			{
				if (WeakNamedModule == null)
					throw new InvalidOperationException("No weak-named assembly has been generated.");
				else
				{
					assemblyBuilder = (AssemblyBuilder) WeakNamedModule.Assembly;
					assemblyFileName = WeakNamedModuleName;
					assemblyFilePath = WeakNamedModule.FullyQualifiedName;
				}
			}

			if (File.Exists(assemblyFilePath))
				File.Delete(assemblyFilePath);

			AddCacheMappings(assemblyBuilder);
			assemblyBuilder.Save(assemblyFileName);
			return assemblyFilePath;
		}

		private void AddCacheMappings(AssemblyBuilder builder)
		{
			Dictionary<CacheKey, string> mappings;

			lock (typeCache.SyncRoot)
			{
				mappings = new Dictionary<CacheKey, string>();
				foreach (DictionaryEntry cacheEntry in typeCache)
					mappings.Add((CacheKey) cacheEntry.Key, ((Type) cacheEntry.Value).FullName);
			}
			CacheMappingsAttribute.ApplyTo(builder, mappings);
		}

		/// <summary>
		/// Loads the generated types from the given assembly into this <see cref="ModuleScope"/>'s cache.
		/// </summary>
		/// <param name="assembly">The assembly to load types from. This assembly must have been saved via <see cref="SaveAssembly(bool)"/> or
		/// <see cref="SaveAssembly()"/>, or it must have the <see cref="CacheMappingsAttribute"/> manually applied.</param>
		/// <remarks>
		/// This method can be used to load previously generated and persisted proxy types from disk into this scope's type cache, eg. in order
		/// to avoid the performance hit associated with proxy generation.
		/// </remarks>
		public void LoadAssemblyIntoCache(Assembly assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException("assembly");

			CacheMappingsAttribute[] cacheMappings =
				(CacheMappingsAttribute[]) assembly.GetCustomAttributes(typeof (CacheMappingsAttribute), false);
			if (cacheMappings.Length == 0)
			{
				string message = string.Format(
					"The given assembly '{0}' does not contain any cache information for generated types.",
					assembly.FullName);
				throw new ArgumentException(message, "assembly");
			}

			lock (typeCache.SyncRoot)
			{
				foreach (KeyValuePair<CacheKey, string> mapping in cacheMappings[0].GetDeserializedMappings())
				{
					Type loadedType = assembly.GetType(mapping.Value);
					if (loadedType != null)
						RegisterInCache(mapping.Key, loadedType);
				}
			}
		}
	}
}