﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

// Simple hack to decompile code from the command line.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using dnlib.DotNet;
using dnSpy.Contracts.Languages;
using dnSpy.Languages.MSBuild;
using dnSpy_Console.Properties;
using ICSharpCode.Decompiler;

namespace dnSpy_Console {
	[Serializable]
	sealed class ErrorException : Exception {
		public ErrorException(string s)
			: base(s) {
		}
	}

	static class Program {
		static int Main(string[] args) {
			if (!dnlib.Settings.IsThreadSafe) {
				Console.WriteLine("dnlib wasn't compiled with THREAD_SAFE defined");
				return 1;
			}

			try {
				return new DnSpyDecompiler().Run(args);
			}
			catch (Exception ex) {
				Console.Error.WriteLine(string.Format("{0}", ex));
				return 1;
			}
		}
	}

	sealed class DnSpyDecompiler : IMSBuildProjectWriterLogger {
		bool isRecursive = false;
		bool useGac = true;
		bool addCorlibRef = true;
		bool createSlnFile = true;
		bool dontMaskErr = false;
		bool unpackResources = true;
		bool createResX = true;
		bool decompileBaml = true;
		bool xmlDocComments = true;
		bool ilComments = false;
		bool ilBytes = true;
		bool tokenComments = true;
		bool sortMembers = true;
		int numThreads;
		int mdToken;
		string typeName;
		ProjectVersion projectVersion = ProjectVersion.VS2010;
		string outputDir;
		string slnName = "solution.sln";
		readonly List<string> files;
		readonly List<string> asmPaths;
		readonly List<string> userGacPaths;
		readonly List<string> gacFiles;
		string language = LanguageConstants.LANGUAGE_CSHARP.ToString();
		readonly DecompilationOptions decompilationOptions;
		readonly ModuleContext moduleContext;
		readonly AssemblyResolver assemblyResolver;
		readonly IBamlDecompiler bamlDecompiler;

		static readonly char PATHS_SEP = Path.PathSeparator;

		public DnSpyDecompiler() {
			this.files = new List<string>();
			this.asmPaths = new List<string>();
			this.userGacPaths = new List<string>();
			this.gacFiles = new List<string>();
			this.decompilationOptions = new DecompilationOptions();
			this.moduleContext = ModuleDef.CreateModuleContext(true);
			this.assemblyResolver = (AssemblyResolver)moduleContext.AssemblyResolver;
			this.assemblyResolver.EnableTypeDefCache = true;
			this.bamlDecompiler = TryLoadBamlDecompiler();
			this.decompileBaml = bamlDecompiler != null;

			var langs = new List<ILanguage>();
			langs.AddRange(GetAllLanguages());
			langs.Sort((a, b) => a.OrderUI.CompareTo(b.OrderUI));
			this.allLanguages = langs.ToArray();
		}

		static IEnumerable<ILanguage> GetAllLanguages() {
			var asmNames = new string[] {
				"Languages.ILSpy.Plugin",
			};
			foreach (var asmName in asmNames) {
				foreach (var l in GetLanguagesInAssembly(asmName))
					yield return l;
			}
		}

		static IEnumerable<ILanguage> GetLanguagesInAssembly(string asmName) {
			var asm = TryLoad(asmName);
			if (asm != null) {
				foreach (var type in asm.GetTypes()) {
					if (!type.IsAbstract && !type.IsInterface && typeof(ILanguageProvider).IsAssignableFrom(type)) {
						var p = (ILanguageProvider)Activator.CreateInstance(type);
						foreach (var l in p.Languages)
							yield return l;
					}
				}
			}
		}

		static IBamlDecompiler TryLoadBamlDecompiler() {
			return TryCreateType<IBamlDecompiler>("dnSpy.BamlDecompiler.Plugin", "dnSpy.BamlDecompiler.BamlDecompiler");
		}

		static Assembly TryLoad(string asmName) {
			try {
				return Assembly.Load(asmName);
			}
			catch {
			}
			return null;
		}

		static T TryCreateType<T>(string asmName, string typeFullName) {
			var asm = TryLoad(asmName);
			var type = asm == null ? null : asm.GetType(typeFullName);
			return type == null ? default(T) : (T)Activator.CreateInstance(type);
		}

		public int Run(string[] args) {
			try {
				ParseCommandLine(args);
				if (allLanguages.Length == 0)
					throw new ErrorException(dnSpy_Console_Resources.NoLanguagesFound);
				if (GetLanguage() == null)
					throw new ErrorException(string.Format(dnSpy_Console_Resources.LanguageXDoesNotExist, language));
				Decompile();
			}
			catch (ErrorException ex) {
				PrintHelp();
				Console.WriteLine(dnSpy_Console_Resources.Error1, ex.Message);
				return 1;
			}
			catch (Exception ex) {
				Dump(ex);
				return 1;
			}
			return errors == 0 ? 0 : 1;
		}

		void PrintHelp() {
			var progName = GetProgramBaseName();
			Console.WriteLine(dnSpy_Console_Resources.Usage, progName, PATHS_SEP);
			Console.WriteLine(dnSpy_Console_Resources.Languages);
			foreach (var lang in AllLanguages)
				Console.WriteLine("  {0} ({1})", lang.UniqueNameUI, lang.UniqueGuid.ToString("B"));
			Console.WriteLine(dnSpy_Console_Resources.Examples, progName);
		}

		void Dump(Exception ex) {
			while (ex != null) {
				Console.WriteLine(dnSpy_Console_Resources.Error1, ex.GetType());
				Console.WriteLine("  {0}", ex.Message);
				Console.WriteLine("  {0}", ex.StackTrace);
				ex = ex.InnerException;
			}
		}

		string GetProgramBaseName() {
			return GetBaseName(Environment.GetCommandLineArgs()[0]);
		}

		string GetBaseName(string name) {
			int index = name.LastIndexOf(Path.DirectorySeparatorChar);
			if (index < 0)
				return name;
			return name.Substring(index + 1);
		}

		void ParseCommandLine(string[] args) {
			if (args.Length == 0)
				throw new ErrorException(dnSpy_Console_Resources.MissingOptions);

			bool canParseCommands = true;
			for (int i = 0; i < args.Length; i++) {
				var arg = args[i];
				var next = i + 1 < args.Length ? args[i + 1] : null;
				if (arg.Length == 0)
					continue;
				if (canParseCommands && arg[0] == '-') {
					switch (arg.Remove(0, 1)) {
					case "":
						canParseCommands = false;
						break;

					case "r":
					case "-recursive":
						isRecursive = true;
						break;

					case "o":
					case "-output-dir":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingOutputDir);
						outputDir = next;
						i++;
						break;

					case "l":
					case "-lang":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingLanguageName);
						language = next;
						i++;
						if (GetLanguage() == null)
							throw new ErrorException(string.Format(dnSpy_Console_Resources.LanguageDoesNotExist, language));
						break;

					case "-asm-path":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingAsmSearchPath);
						asmPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
						i++;
						break;

					case "-user-gac":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingUserGacPath);
						userGacPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
						i++;
						break;

					case "-no-gac":
						useGac = false;
						break;

					case "-no-stdlib":
						addCorlibRef = false;
						break;

					case "-no-sln":
						createSlnFile = false;
						break;

					case "-sln-name":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingSolutionName);
						slnName = next;
						i++;
						if (Path.IsPathRooted(slnName))
							throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidSolutionName, slnName));
						break;

					case "-dont-mask-merr":
						dontMaskErr = true;
						break;

					case "-threads":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingNumberOfThreads);
						i++;
						if (!int.TryParse(next, out numThreads))
							throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidInteger, next));
						break;

					case "-vs":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingVSVersion);
						i++;
						int vsVer;
						if (!int.TryParse(next, out vsVer))
							throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidInteger, next));
						switch (vsVer) {
						case 2005: projectVersion = ProjectVersion.VS2005; break;
						case 2008: projectVersion = ProjectVersion.VS2008; break;
						case 2010: projectVersion = ProjectVersion.VS2010; break;
						case 2012: projectVersion = ProjectVersion.VS2012; break;
						case 2013: projectVersion = ProjectVersion.VS2013; break;
						case 2015: projectVersion = ProjectVersion.VS2015; break;
						default: throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidVSVersion, vsVer));
						}
						break;

					case "-no-resources":
						unpackResources = false;
						break;

					case "-no-resx":
						createResX = false;
						break;

					case "-no-baml":
						decompileBaml = false;
						break;

					case "t":
					case "-type":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingTypeName);
						i++;
						typeName = next;
						break;

					case "-md":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingMDToken);
						i++;
						bool parsedToken;
						if (next.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || next.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
							parsedToken = int.TryParse(next.Substring(2), NumberStyles.HexNumber, null, out mdToken);
						else
							parsedToken = int.TryParse(next, out mdToken);
						if (!parsedToken)
							throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidInteger, next));
						break;

					case "-gac-file":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingGacFile);
						i++;
						gacFiles.Add(next);
						break;

					case "-no-xmldoc":
						xmlDocComments = false;
						break;

					case "-il-comments":
						ilComments = true;
						break;

					case "-no-il-bytes":
						ilBytes = false;
						break;

					case "-no-tokens":
						tokenComments = false;
						break;

					case "-no-sort":
						sortMembers = false;
						break;

					case "-order":
						if (next == null)
							throw new ErrorException(dnSpy_Console_Resources.MissingOrderArg);
						i++;
						if (next.Length != 5)
							throw new ErrorException(dnSpy_Console_Resources.InvalidOrderArg);
						decompilationOptions.DecompilerSettings.DecompilationObject0 = GetDecompilationObject(next[0]);
						decompilationOptions.DecompilerSettings.DecompilationObject1 = GetDecompilationObject(next[1]);
						decompilationOptions.DecompilerSettings.DecompilationObject2 = GetDecompilationObject(next[2]);
						decompilationOptions.DecompilerSettings.DecompilationObject3 = GetDecompilationObject(next[3]);
						decompilationOptions.DecompilerSettings.DecompilationObject4 = GetDecompilationObject(next[4]);
						break;

					default:
						throw new ErrorException(string.Format(dnSpy_Console_Resources.InvalidOption, arg));
					}
				}
				else
					files.Add(arg);
			}
		}

		static DecompilationObject GetDecompilationObject(char c) {
			switch (c) {
			case 't': return DecompilationObject.NestedTypes;
			case 'f': return DecompilationObject.Fields;
			case 'e': return DecompilationObject.Events;
			case 'p': return DecompilationObject.Properties;
			case 'm': return DecompilationObject.Methods;
			default: throw new ErrorException(dnSpy_Console_Resources.InvalidOrderArg);
			}
		}

		void AddSearchPath(string dir) {
			if (Directory.Exists(dir) && !addedPaths.Contains(dir)) {
				addedPaths.Add(dir);
				assemblyResolver.PreSearchPaths.Add(dir);
			}
		}
		readonly HashSet<string> addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Decompile() {
			foreach (var dir in asmPaths)
				AddSearchPath(dir);
			foreach (var dir in userGacPaths)
				AddSearchPath(dir);
			assemblyResolver.UseGAC = useGac;
			decompilationOptions.DontShowCreateMethodBodyExceptions = dontMaskErr;
			decompilationOptions.DecompilerSettings.ShowXmlDocumentation = xmlDocComments;
			decompilationOptions.DecompilerSettings.ShowILComments = ilComments;
			decompilationOptions.DecompilerSettings.ShowILBytes = ilBytes;
			decompilationOptions.DecompilerSettings.ShowTokenAndRvaComments = tokenComments;
			decompilationOptions.DecompilerSettings.SortMembers = sortMembers;

			var files = new List<ProjectModuleOptions>(GetDotNetFiles());
			if (mdToken != 0 || typeName != null) {
				if (files.Count == 0)
					throw new ErrorException(dnSpy_Console_Resources.MissingDotNetFilename);
				if (files.Count != 1)
					throw new ErrorException(dnSpy_Console_Resources.OnlyOneFileCanBeDecompiled);

				IMemberDef member;
				if (typeName != null)
					member = FindType(files[0].Module, typeName);
				else
					member = files[0].Module.ResolveToken(mdToken) as IMemberDef;
				if (member == null) {
					if (typeName != null)
						throw new ErrorException(string.Format(dnSpy_Console_Resources.CouldNotFindTypeX, typeName));
					throw new ErrorException(dnSpy_Console_Resources.InvalidToken);
				}

				var writer = Console.Out;
				var output = new PlainTextOutput(writer);

				var lang = GetLanguage();
				if (member is MethodDef)
					lang.Decompile((MethodDef)member, output, decompilationOptions);
				else if (member is FieldDef)
					lang.Decompile((FieldDef)member, output, decompilationOptions);
				else if (member is PropertyDef)
					lang.Decompile((PropertyDef)member, output, decompilationOptions);
				else if (member is EventDef)
					lang.Decompile((EventDef)member, output, decompilationOptions);
				else if (member is TypeDef)
					lang.Decompile((TypeDef)member, output, decompilationOptions);
				else
					throw new ErrorException(dnSpy_Console_Resources.InvalidMemberToDecompile);
			}
			else {
				if (string.IsNullOrEmpty(outputDir))
					throw new ErrorException(dnSpy_Console_Resources.MissingOutputDir);
				if (GetLanguage().ProjectFileExtension == null)
					throw new ErrorException(string.Format(dnSpy_Console_Resources.LanguageXDoesNotSupportProjects, GetLanguage().UniqueNameUI));

				var options = new ProjectCreatorOptions(outputDir, decompilationOptions.CancellationToken);
				options.Logger = this;
				options.ProjectVersion = projectVersion;
				options.NumberOfThreads = numThreads;
				options.ProjectModules.AddRange(files);
				options.UserGACPaths.AddRange(userGacPaths);
				if (createSlnFile && !string.IsNullOrEmpty(slnName))
					options.SolutionFilename = slnName;
				var creator = new MSBuildProjectCreator(options);
				creator.Create();
			}
		}

		static TypeDef FindType(ModuleDef module, string name) {
			return FindTypeFullName(module, name, StringComparer.Ordinal) ??
				FindTypeFullName(module, name, StringComparer.OrdinalIgnoreCase) ??
				FindTypeName(module, name, StringComparer.Ordinal) ??
				FindTypeName(module, name, StringComparer.OrdinalIgnoreCase);
		}

		static TypeDef FindTypeFullName(ModuleDef module, string name, StringComparer comparer) {
			return module.GetTypes().FirstOrDefault(a => 
					comparer.Equals(a.FullName, name) ||
					comparer.Equals(a.ReflectionFullName, name) ||
					comparer.Equals(CleanTypeName(a.FullName), name) ||
					comparer.Equals(CleanTypeName(a.ReflectionFullName), name)
				);
		}

		static TypeDef FindTypeName(ModuleDef module, string name, StringComparer comparer) {
			return module.GetTypes().FirstOrDefault(a =>
					comparer.Equals(a.Name, name) ||
					comparer.Equals(a.ReflectionName, name) ||
					comparer.Equals(CleanTypeName(a.Name), name) ||
					comparer.Equals(CleanTypeName(a.ReflectionName), name)
				);
		}

		static string CleanTypeName(string s) {
			int i = s.LastIndexOf('`');
			if (i < 0)
				return s;
			return s.Substring(0, i);
		}

		IEnumerable<ProjectModuleOptions> GetDotNetFiles() {
			foreach (var file in files) {
				if (File.Exists(file)) {
					var info = OpenNetFile(file);
					if (info == null)
						throw new Exception(string.Format(dnSpy_Console_Resources.NotDotNetFile, file));
					yield return info;
				}
				else if (Directory.Exists(file)) {
					foreach (var info in DumpDir(file, null))
						yield return info;
				}
				else {
					var path = Path.GetDirectoryName(file);
					var name = Path.GetFileName(file);
					if (Directory.Exists(path)) {
						foreach (var info in DumpDir(path, name))
							yield return info;
					}
					else
						throw new ErrorException(string.Format(dnSpy_Console_Resources.FileOrDirDoesNotExist, file));
				}
			}

			foreach (var asmName in gacFiles) {
				var asm = this.assemblyResolver.Resolve(new AssemblyNameInfo(asmName), null);
				if (asm == null)
					throw new ErrorException(string.Format(dnSpy_Console_Resources.CouldNotResolveGacFileX, asmName));
				yield return CreateProjectModuleOptions(asm.ManifestModule);
			}
		}

		IEnumerable<ProjectModuleOptions> DumpDir(string path, string pattern) {
			pattern = pattern ?? "*";
			Stack<string> stack = new Stack<string>();
			stack.Push(path);
			while (stack.Count > 0) {
				path = stack.Pop();
				foreach (var info in DumpDir2(path, pattern))
					yield return info;
				if (isRecursive) {
					foreach (var di in GetDirs(path))
						stack.Push(di.FullName);
				}
			}
		}

		IEnumerable<DirectoryInfo> GetDirs(string path) {
			IEnumerable<FileSystemInfo> fsysIter = null;
			try {
				fsysIter = new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
			catch (SecurityException) {
			}
			if (fsysIter == null)
				yield break;

			foreach (var info in fsysIter) {
				if ((info.Attributes & System.IO.FileAttributes.Directory) == 0)
					continue;
				DirectoryInfo di = null;
				try {
					di = new DirectoryInfo(info.FullName);
				}
				catch (IOException) {
				}
				catch (UnauthorizedAccessException) {
				}
				catch (SecurityException) {
				}
				if (di != null)
					yield return di;
			}
		}

		IEnumerable<ProjectModuleOptions> DumpDir2(string path, string pattern) {
			pattern = pattern ?? "*";
			foreach (var fi in GetFiles(path, pattern)) {
				var info = OpenNetFile(fi.FullName);
				if (info != null)
					yield return info;
			}
		}

		IEnumerable<FileInfo> GetFiles(string path, string pattern) {
			IEnumerable<FileSystemInfo> fsysIter = null;
			try {
				fsysIter = new DirectoryInfo(path).EnumerateFileSystemInfos(pattern, SearchOption.TopDirectoryOnly);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
			catch (SecurityException) {
			}
			if (fsysIter == null)
				yield break;

			foreach (var info in fsysIter) {
				if ((info.Attributes & System.IO.FileAttributes.Directory) != 0)
					continue;
				FileInfo fi = null;
				try {
					fi = new FileInfo(info.FullName);
				}
				catch (IOException) {
				}
				catch (UnauthorizedAccessException) {
				}
				catch (SecurityException) {
				}
				if (fi != null)
					yield return fi;
			}
		}

		ProjectModuleOptions OpenNetFile(string file) {
			try {
				file = Path.GetFullPath(file);
				if (!File.Exists(file))
					return null;
				return CreateProjectModuleOptions(ModuleDefMD.Load(file, moduleContext));
			}
			catch {
			}
			return null;
		}

		ProjectModuleOptions CreateProjectModuleOptions(ModuleDef mod) {
			mod.EnableTypeDefFindCache = true;
			moduleContext.AssemblyResolver.AddToCache(mod);
			AddSearchPath(Path.GetDirectoryName(mod.Location));
			var proj = new ProjectModuleOptions(mod, GetLanguage(), decompilationOptions);
			proj.DontReferenceStdLib = !addCorlibRef;
			proj.UnpackResources = unpackResources;
			proj.CreateResX = createResX;
			proj.DecompileXaml = decompileBaml && bamlDecompiler != null;
			var o = BamlDecompilerOptions.Create(GetLanguage());
			if (bamlDecompiler != null)
				proj.DecompileBaml = (a, b, c, d) => bamlDecompiler.Decompile(a, b, c, o, d);
			return proj;
		}

		ILanguage GetLanguage() {
			Guid guid;
			bool hasGuid = Guid.TryParse(language, out guid);
			return AllLanguages.FirstOrDefault(a => {
				if (StringComparer.OrdinalIgnoreCase.Equals(language, a.UniqueNameUI))
					return true;
				if (hasGuid && (guid.Equals(a.UniqueGuid) || guid.Equals(a.GenericGuid)))
					return true;
				return false;
			});
		}

		ILanguage[] AllLanguages {
			get { return allLanguages; }
		}
		readonly ILanguage[] allLanguages;

		public void Error(string message) {
			errors++;
			Console.Error.WriteLine(string.Format(dnSpy_Console_Resources.Error1, message));
		}
		int errors;
	}
}
