﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Debugger.Interop;
using Debugger.Interop.CorDebug;
using Debugger.Interop.CorSym;
using Debugger.Interop.MetaData;
using Debugger.MetaData;
using ICSharpCode.NRefactory.TypeSystem;

namespace Debugger
{
	public class Module: DebuggerObject, IDisposable
	{
		AppDomain appDomain;
		Process   process;
		
		bool   unloaded = false;
		string name;
		string fullPath = string.Empty;
		
		int orderOfLoading = 0;
		ICorDebugModule corModule;
		ISymUnmanagedReader symReader;
		MetaDataImport metaData;
		
		IUnresolvedAssembly unresolvedAssembly;
		internal Dictionary<string, DebugType> LoadedDebugTypes = new Dictionary<string, DebugType>();
		
		public IUnresolvedAssembly UnresolvedAssembly {
			get { return unresolvedAssembly; }
		}
		
		public IAssembly Assembly {
			get { return unresolvedAssembly.Resolve(appDomain.Compilation.TypeResolveContext); }
		}
		
		public AppDomain AppDomain {
			get { return appDomain; }
		}
		
		public Process Process {
			get { return process; }
		}
		
		NDebugger Debugger {
			get { return this.AppDomain.Process.Debugger; }
		}
		
		[Debugger.Tests.Ignore]
		public MetaDataImport MetaData {
			get {
				return metaData;
			}
		}
		
		public bool Unloaded {
			get {
				return unloaded;
			}
		}
		
		[Debugger.Tests.Ignore]
		public ISymUnmanagedReader SymReader {
			get {
				return symReader;
			}
		}
		
		[Debugger.Tests.Ignore]
		public ISymUnmanagedDocument[] SymDocuments {
			get {
				ISymUnmanagedDocument[] docs;
				uint maxCount = 2;
				uint fetched;
				do {
					maxCount *= 8;
					docs = new ISymUnmanagedDocument[maxCount];
					symReader.GetDocuments(maxCount, out fetched, docs);
				} while (fetched == maxCount);
				Array.Resize(ref docs, (int)fetched);
				return docs;
			}
		}
		
		[Debugger.Tests.Ignore]
		public ICorDebugModule CorModule {
			get { return corModule; }
		}
		
		[Debugger.Tests.Ignore]
		public ICorDebugModule2 CorModule2 {
			get { return (ICorDebugModule2)corModule; }
		}
		
		[Debugger.Tests.Ignore]
		public ulong BaseAdress {
			get {
				return this.CorModule.GetBaseAddress();
			}
		}
		
		public bool IsDynamic {
			get {
				return this.CorModule.IsDynamic() == 1;
			}
		}
		
		public bool IsInMemory {
			get {
				return this.CorModule.IsInMemory() == 1;
			}
		}
		
		internal uint AppDomainID {
			get {
				return this.CorModule.GetAssembly().GetAppDomain().GetID();
			}
		}
		
		public string Name {
			get {
				return name;
			}
		}
		
		[Debugger.Tests.Ignore]
		public string FullPath {
			get {
				return fullPath;
			}
		}
		
		public bool HasSymbols {
			get {
				return symReader != null;
			}
		}
		
		public int OrderOfLoading {
			get {
				return orderOfLoading;
			}
			set {
				orderOfLoading = value;
			}
		}
		
		[Debugger.Tests.Ignore]
		public CorDebugJITCompilerFlags JITCompilerFlags
		{
			get
			{
				uint retval = ((ICorDebugModule2)corModule).GetJITCompilerFlags();
				return (CorDebugJITCompilerFlags)retval;
			}
			set
			{
				// ICorDebugModule2.SetJITCompilerFlags can return successful HRESULTS other than S_OK.
				// Since we have asked the COMInterop layer to preservesig, we need to marshal any failing HRESULTS.
				((ICorDebugModule2)corModule).SetJITCompilerFlags((uint)value);
			}
		}
		
		/// <summary> Returns all non-generic types defined in the module </summary>
		/// <remarks> Generic types can not be returned, because we do not know how to instanciate them </remarks>
		public List<DebugType> GetDefinedTypes()
		{
			List<DebugType> types = new List<DebugType>();
			foreach(TypeDefProps typeDef in this.MetaData.EnumTypeDefProps()) {
				if (this.MetaData.EnumGenericParams(typeDef.Token).Length == 0) {
					types.Add(DebugType.CreateFromTypeDefOrRef(this, null, typeDef.Token, null));
				}
			}
			return types;
		}
		
		/// <summary> Get names of all generic and non-generic types defined in this module </summary>
		public List<string> GetNamesOfDefinedTypes()
		{
			List<string> names = new List<string>();
			foreach(TypeDefProps typeProps in this.MetaData.EnumTypeDefProps()) {
				names.Add(typeProps.Name);
			}
			return names;
		}
		
		internal Module(AppDomain appDomain, ICorDebugModule corModule)
		{
			this.appDomain = appDomain;
			this.process = appDomain.Process;
			this.corModule = corModule;
			
			unresolvedAssembly = TypeSystemExtensions.LoadModule(this, corModule);
			metaData = new MetaDataImport(corModule);
			
			if (IsDynamic || IsInMemory) {
				name     = corModule.GetName();
			} else {
				fullPath = corModule.GetName();
				name     = System.IO.Path.GetFileName(FullPath);
			}
			
			SetJITCompilerFlags();
			
			LoadSymbolsFromDisk(process.Options.SymbolsSearchPaths);
			ResetJustMyCodeStatus();
			LoadSymbolsDynamic();
		}
		
		public void UnloadSymbols()
		{
			if (symReader != null) {
				// The interface is not always supported, I did not manage to reproduce it, but the
				// last callbacks in the user's log were UnloadClass and UnloadModule so I guess
				// it has something to do with dynamic modules.
				if (symReader is ISymUnmanagedDispose) {
					((ISymUnmanagedDispose)symReader).Destroy();
				}
				symReader = null;
			}
		}
		
		/// <summary>
		/// Load symblos for on-disk module
		/// </summary>
		public void LoadSymbolsFromDisk(string[] symbolsSearchPaths)
		{
			if (!IsDynamic && !IsInMemory) {
				if (symReader == null) {
					symReader = metaData.GetSymReader(fullPath, string.Join("; ", symbolsSearchPaths ?? new string[0]));
					if (symReader != null) {
						process.TraceMessage("Loaded symbols from disk for " + this.Name);
						OnSymbolsUpdated();
					}
				}
			}
		}
		
		/// <summary>
		/// Load symbols for in-memory module
		/// </summary>
		public void LoadSymbolsFromMemory(IStream pSymbolStream)
		{
			if (this.IsInMemory) {
				UnloadSymbols();
				
				symReader = metaData.GetSymReader(pSymbolStream);
				if (symReader != null) {
					process.TraceMessage("Loaded symbols from memory for " + this.Name);
				} else {
					process.TraceMessage("Failed to load symbols from memory");
				}
				
				OnSymbolsUpdated();
			}
		}
		
		/// <summary>
		/// Load symbols for dynamic module
		/// (as of .NET 4.0)
		/// </summary>
		public void LoadSymbolsDynamic()
		{
			if (this.CorModule is ICorDebugModule3 && this.IsDynamic) {
				Guid guid = new Guid(0, 0, 0, 0xc0, 0, 0, 0, 0, 0, 0, 70);
				try {
					symReader = (ISymUnmanagedReader)((ICorDebugModule3)this.CorModule).CreateReaderForInMemorySymbols(guid);
				} catch (COMException e) {
					// 0x80131C3B The application did not supply symbols when it loaded or created this module, or they are not yet available.
					if ((uint)e.ErrorCode == 0x80131C3B) {
						process.TraceMessage("Failed to load dynamic symbols for " + this.Name);
						return;
					}
					throw;
				}
				TrackedComObjects.Track(symReader);
				process.TraceMessage("Loaded dynamic symbols for " + this.Name);
				OnSymbolsUpdated();
			}
		}
		
		void OnSymbolsUpdated()
		{
			foreach (Breakpoint b in this.Debugger.Breakpoints) {
				b.SetBreakpoint(this);
			}
			ResetJustMyCodeStatus();
		}
		
		void SetJITCompilerFlags()
		{
			if (Process.DebugMode != DebugModeFlag.Default) {
				// translate DebugModeFlags to JITCompilerFlags
				CorDebugJITCompilerFlags jcf = MapDebugModeToJITCompilerFlags(Process.DebugMode);

				try
				{
					this.JITCompilerFlags = jcf;

					// Flags may succeed but not set all bits, so requery.
					CorDebugJITCompilerFlags jcfActual = this.JITCompilerFlags;
					
					#if DEBUG
					if (jcf != jcfActual)
						Console.WriteLine("Couldn't set all flags. Actual flags:" + jcfActual.ToString());
					else
						Console.WriteLine("Actual flags:" + jcfActual.ToString());
					#endif
				}
				catch (COMException ex)
				{
					// we'll ignore the error if we cannot set the jit flags
					Console.WriteLine(string.Format("Failed to set flags with hr=0x{0:x}", ex.ErrorCode));
				}
			}
		}
		
		/// <summary> Sets all code as being 'my code'.  The code will be gradually
		/// set to not-user-code as encountered according to stepping options </summary>
		public void ResetJustMyCodeStatus()
		{
			uint unused = 0;
			if (process.Options.StepOverNoSymbols && !this.HasSymbols) {
				// Optimization - set the code as non-user right away
				this.CorModule2.SetJMCStatus(0, 0, ref unused);
				return;
			}
			try {
				this.CorModule2.SetJMCStatus(process.Options.EnableJustMyCode ? 1 : 0, 0, ref unused);
			} catch (COMException e) {
				// Cannot use JMC on this code (likely wrong JIT settings).
				if ((uint)e.ErrorCode == 0x80131323) {
					process.TraceMessage("Cannot use JMC on this code.  Release build?");
					return;
				}
				throw;
			}
		}
		
		public void ApplyChanges(byte[] metadata, byte[] il)
		{
			this.CorModule2.ApplyChanges((uint)metadata.Length, metadata, (uint)il.Length, il);
		}
		
		public void Dispose()
		{
			UnloadSymbols();
			unloaded = true;
		}
		
		public override string ToString()
		{
			return string.Format("{0}", this.Name);
		}
		
		public static CorDebugJITCompilerFlags MapDebugModeToJITCompilerFlags(DebugModeFlag debugMode)
		{
			CorDebugJITCompilerFlags jcf;
			switch (debugMode)
			{
				case DebugModeFlag.Optimized:
					jcf = CorDebugJITCompilerFlags.CORDEBUG_JIT_DEFAULT; // DEFAULT really means force optimized.
					break;
				case DebugModeFlag.Debug:
					jcf = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;
					break;
				case DebugModeFlag.Enc:
					jcf = CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC;
					break;
				default:
					// we don't have mapping from default to "default",
					// therefore we'll use DISABLE_OPTIMIZATION.
					jcf = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;
					break;
			}
			return jcf;
		}
	}
}
