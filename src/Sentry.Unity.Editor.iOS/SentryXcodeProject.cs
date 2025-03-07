using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Sentry.Extensibility;

// The decision to use reflection here came after many attempts to avoid it. Allowing customers to export an xcode
// project outside a Mac while not requiring users to install the iOS tools on Windows and Linux became a challenge
// More context: https://github.com/getsentry/sentry-unity/issues/400, https://github.com/getsentry/sentry-unity/issues/588
// using UnityEditor.iOS.Xcode;
// using UnityEditor.iOS.Xcode.Extensions;

namespace Sentry.Unity.Editor.iOS
{
    internal class SentryXcodeProject : IDisposable
    {
        internal const string FrameworkName = "Sentry.xcframework";
        internal const string BridgeName = "SentryNativeBridge.m";
        internal const string OptionsName = "SentryOptions.m";
        internal const string SymbolUploadPhaseName = "SymbolUpload";

        internal static readonly string MainPath = Path.Combine("MainApp", "main.mm");
        private readonly string _optionsPath = Path.Combine("MainApp", OptionsName);
        private readonly string _uploadScript = @"
process_upload_symbols() {{
    ./{0} upload-dif --force-foreground {1} $BUILT_PRODUCTS_DIR 2>&1 | tee ./sentry-symbols-upload.log
}}

export SENTRY_PROPERTIES=sentry.properties
if [ ""$ENABLE_BITCODE"" = ""NO"" ] ; then
    echo ""Uploading debug symbols (Bitcode disabled).""
    process_upload_symbols
else
    echo ""Bitcode is enabled""
    if [ ""$ACTION"" = ""install"" ] ; then
        echo ""Uploading debug symbols and bcsymbolmaps (Bitcode enabled).""
        process_upload_symbols
    else
        echo ""Skipping debug symbol upload because Bitcode is enabled and this is a non-install build.""
    fi
fi
";

        private readonly Type _pbxProjectType = null!;              // Set in constructor or throws
        private readonly Type _pbxProjectExtensionsType = null!;    // Set in constructor or throws
        private readonly object _project = null!;                   // Set in constructor or throws

        private readonly string _projectRoot;
        private readonly string _projectPath;

        private string _mainTargetGuid = null!;             // Set when opening the project
        private string _unityFrameworkTargetGuid = null!;   // Set when opening the project

        internal SentryXcodeProject(string projectRoot)
        {
            var xcodeAssembly = Assembly.Load("UnityEditor.iOS.Extensions.Xcode");
            _pbxProjectType = xcodeAssembly.GetType("UnityEditor.iOS.Xcode.PBXProject");
            _pbxProjectExtensionsType = xcodeAssembly.GetType("UnityEditor.iOS.Xcode.Extensions.PBXProjectExtensions");

            _project = Activator.CreateInstance(_pbxProjectType);

            _projectRoot = projectRoot;
            _projectPath = (string)_pbxProjectType.GetMethod("GetPBXProjectPath", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new[] { _projectRoot });
        }

        public static SentryXcodeProject Open(string path)
        {
            var xcodeProject = new SentryXcodeProject(path);
            xcodeProject.ReadFromProjectFile();

            return xcodeProject;
        }

        internal void ReadFromProjectFile()
        {
            if (!File.Exists(_projectPath))
            {
                throw new FileNotFoundException("Could not locate generated Xcode project at", _projectPath);
            }

            _pbxProjectType.GetMethod("ReadFromString", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new[] { File.ReadAllText(_projectPath) });
            _mainTargetGuid = (string)_pbxProjectType.GetMethod("GetUnityMainTargetGuid", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, null);
            _unityFrameworkTargetGuid = (string)_pbxProjectType.GetMethod("GetUnityFrameworkTargetGuid", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, null);
        }

        public void AddSentryFramework()
        {
            var relativeFrameworkPath = Path.Combine("Frameworks", FrameworkName);
            var frameworkGuid = (string)_pbxProjectType.GetMethod("AddFile", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new object[] { relativeFrameworkPath, relativeFrameworkPath, 1 }); // 1 is PBXSourceTree.Source

            // Embedding the framework because it's dynamic and needed at runtime
            _pbxProjectExtensionsType.GetMethod("AddFileToEmbedFrameworks", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object?[] { _project, _mainTargetGuid, frameworkGuid, null });

            SetSearchPathBuildProperty("$(inherited)");
            SetSearchPathBuildProperty("$(PROJECT_DIR)/Frameworks/");

            var setBuildPropertyMethod = _pbxProjectType.GetMethod("SetBuildProperty", new[] { typeof(string), typeof(string), typeof(string) });
            setBuildPropertyMethod.Invoke(_project, new object[] { _mainTargetGuid, "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym" });
            setBuildPropertyMethod.Invoke(_project, new object[] { _unityFrameworkTargetGuid, "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym" });

            _pbxProjectType.GetMethod("AddBuildProperty", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(_project, new object[] { _mainTargetGuid, "OTHER_LDFLAGS", "-ObjC" });

            // Getting the Link With Binary phase
            var getBuildPhaseMethod = _pbxProjectType.GetMethod("GetFrameworksBuildPhaseByTarget", new[] { typeof(string) });
            var mainBuildPhaseGuid = (string)getBuildPhaseMethod.Invoke(_project, new object[] { _mainTargetGuid });
            var unityFrameworkBuildPhaseGuid = (string)getBuildPhaseMethod.Invoke(_project, new object[] { _unityFrameworkTargetGuid });

            // Linking With Binary
            var addFileToBuildSectionMethod = _pbxProjectType.GetMethod("AddFileToBuildSection", new[] { typeof(string), typeof(string), typeof(string) });
            addFileToBuildSectionMethod.Invoke(_project, new object[] { _mainTargetGuid, mainBuildPhaseGuid, frameworkGuid });
            addFileToBuildSectionMethod.Invoke(_project, new object[] { _unityFrameworkTargetGuid, unityFrameworkBuildPhaseGuid, frameworkGuid });
        }

        public void AddSentryNativeBridge()
        {
            var relativeBridgePath = Path.Combine("Libraries", SentryPackageInfo.GetName(), BridgeName);
            var bridgeGuid = (string)_pbxProjectType.GetMethod("AddFile", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new object[] { relativeBridgePath, relativeBridgePath, 1 }); // 1 is PBXSourceTree.Source

            _pbxProjectType.GetMethod("AddFileToBuild", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new[] { _unityFrameworkTargetGuid, bridgeGuid });
        }

        // Used for testing
        internal void SetSearchPathBuildProperty(string path)
        {
            _pbxProjectType.GetMethod("AddBuildProperty", new[] { typeof(string[]), typeof(string), typeof(string) })
                .Invoke(_project, new object[] { new[] { _mainTargetGuid, _unityFrameworkTargetGuid }, "FRAMEWORK_SEARCH_PATHS", path });
        }

        public void AddBuildPhaseSymbolUpload(IDiagnosticLogger? logger, SentryCliOptions sentryCliOptions)
        {
            if (MainTargetContainsSymbolUploadBuildPhase())
            {
                logger?.LogDebug("Build phase '{0}' already added.", SymbolUploadPhaseName);
                return;
            }

            var uploadDifArguments = "--il2cpp-mapping";
            if (sentryCliOptions.UploadSources)
            {
                uploadDifArguments += " --include-sources";
            }
            var uploadScript = string.Format(_uploadScript, SentryCli.SentryCliMacOS, uploadDifArguments);

            _pbxProjectType.GetMethod("AddShellScriptBuildPhase", new[] { typeof(string), typeof(string), typeof(string), typeof(string) })
                .Invoke(_project, new object[] { _mainTargetGuid, SymbolUploadPhaseName, "/bin/sh", uploadScript });
        }

        public void AddNativeOptions(SentryUnityOptions options, Action<string, SentryUnityOptions> nativeOptionFileCreation)
        {
            nativeOptionFileCreation.Invoke(Path.Combine(_projectRoot, _optionsPath), options);
            _pbxProjectType.GetMethod("AddFile", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new object[] { _optionsPath, _optionsPath, 1 }); // 1 is PBXSourceTree.Source
        }

        public void AddSentryToMain(SentryUnityOptions options) =>
            NativeMain.AddSentry(Path.Combine(_projectRoot, MainPath), options.DiagnosticLogger);

        internal bool MainTargetContainsSymbolUploadBuildPhase()
        {
            var allBuildPhases = (string[])_pbxProjectType.GetMethod("GetAllBuildPhasesForTarget", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new object[] { _mainTargetGuid });
            var getBuildPhaseNameMethod = _pbxProjectType.GetMethod("GetBuildPhaseName", BindingFlags.Public | BindingFlags.Instance);

            return allBuildPhases.Any(buildPhase =>
            {
                var buildPhaseName = (string)getBuildPhaseNameMethod.Invoke(_project, new[] { buildPhase });
                return buildPhaseName == SymbolUploadPhaseName;
            });
        }

        internal string ProjectToString() =>
            (string)_pbxProjectType.GetMethod("WriteToString", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, null);

        public void Dispose() =>
            _pbxProjectType.GetMethod("WriteToFile", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(_project, new[] { _projectPath });
    }
}
