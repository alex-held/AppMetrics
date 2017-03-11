#addin Cake.Coveralls
#addin Cake.ReSharperReports
#addin Cake.Incubator
#addin Cake.Compression

#tool "nuget:?package=xunit.runner.console"
#tool "nuget:?package=JetBrains.dotCover.CommandLineTools"
#tool "nuget:?package=OpenCover"
#tool "nuget:?package=ReSharperReports"
#tool "nuget:?package=JetBrains.ReSharper.CommandLineTools"
#tool "nuget:?package=coveralls.io"

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target                      = Argument("target", "Default");
var configuration               = HasArgument("BuildConfiguration") ? Argument<string>("BuildConfiguration") :
                                  EnvironmentVariable("BuildConfiguration") != null ? EnvironmentVariable("BuildConfiguration") : "Release";
var coverWith					= HasArgument("CoverWith") ? Argument<string>("CoverWith") :
                                  EnvironmentVariable("CoverWith") != null ? EnvironmentVariable("CoverWith") : "OpenCover"; // None, DotCover, OpenCover
var skipReSharperCodeInspect    = Argument("SkipCodeInspect", false) || !IsRunningOnWindows();
var preReleaseSuffix            = HasArgument("PreReleaseSuffix") ? Argument<string>("PreReleaseSuffix") :
	                              (AppVeyor.IsRunningOnAppVeyor && AppVeyor.Environment.Repository.Tag.IsTag) ? null :
                                  EnvironmentVariable("PreReleaseSuffix") != null ? EnvironmentVariable("PreReleaseSuffix") : "ci";
var buildNumber                 = HasArgument("BuildNumber") ? Argument<int>("BuildNumber") :
                                  AppVeyor.IsRunningOnAppVeyor ? AppVeyor.Environment.Build.Number :
                                  TravisCI.IsRunningOnTravisCI ? TravisCI.Environment.Build.BuildNumber :
                                  EnvironmentVariable("BuildNumber") != null ? int.Parse(EnvironmentVariable("BuildNumber")) : 0;

//////////////////////////////////////////////////////////////////////
// DEFINE FILES & DIRECTORIES
//////////////////////////////////////////////////////////////////////
var packDirs                    = new [] { Directory("./src/App.Metrics"), Directory("./src/App.Metrics.Concurrency"), Directory("./src/App.Metrics.Extensions.Middleware"), Directory("./src/App.Metrics.Extensions.Mvc"), Directory("./src/App.Metrics.Formatters.Json") };
var artifactsDir                = (DirectoryPath) Directory("./artifacts");
var testResultsDir              = (DirectoryPath) artifactsDir.Combine("test-results");
var coverageResultsDir          = (DirectoryPath) artifactsDir.Combine("coverage");
var reSharperReportsDir         = (DirectoryPath) artifactsDir.Combine("resharper-reports");
var testCoverageOutputFilePath  = coverageResultsDir.CombineWithFilePath("coverage.xml");
var testOCoverageOutputFilePath = coverageResultsDir.CombineWithFilePath("openCoverCoverage.xml");
var htmlCoverageReport			= coverageResultsDir.FullPath + "/coverage.html";
var mergedCoverageSnapshots		= coverageResultsDir.FullPath + "/coverage.dcvr";
var xmlCoverageReport			= coverageResultsDir.FullPath + "/coverage.xml";
var packagesDir                 = artifactsDir.Combine("packages");
var resharperSettings			= "./AppMetrics.sln.DotSettings";
var inspectCodeXml				= string.Format("{0}/inspectCode.xml", reSharperReportsDir);
var inspectCodeHtml				= string.Format("{0}/inspectCode.html", reSharperReportsDir);
var solutionFile				= "./AppMetrics.sln";
var solution					= ParseSolution(new FilePath(solutionFile));

//////////////////////////////////////////////////////////////////////
// DEFINE PARAMS
//////////////////////////////////////////////////////////////////////
var coverallsToken              = Context.EnvironmentVariable("COVERALLS_REPO_TOKEN");
var openCoverFilter				= "+[App.Metrics*]* -[xunit.*]* -[*.Facts]*";
var openCoverExcludeFile        = "*/*Designer.cs;*/*.g.cs;*/*.g.i.cs";
var coverIncludeFilter			= "+:App.Metrics*";
var coverExcludeFilter			= "-:*.Facts";
var excludeFromCoverage			= "*.AppMetricsExcludeFromCodeCoverage*";

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectory(artifactsDir);    
});

Task("Restore")
    .IsDependentOn("Clean")    
    .Does(() =>
{	
	var settings = new DotNetCoreRestoreSettings
    {        
        Sources = new [] { "https://api.nuget.org/v3/index.json" }		
    };

	var projects = solution.GetProjects();

	foreach(var project in projects)
	{
	    DotNetCoreRestore(project.Path.ToString(), settings);
    }    
});

Task("Build")    
    .IsDependentOn("Restore")
    .Does(() =>
{	
	var projects = solution.GetProjects();
    
	var settings = new DotNetCoreBuildSettings  { Configuration = configuration };

    foreach(var project in projects)
    {		
        DotNetCoreBuild(project.Path.ToString(), settings);
    }    
});

Task("Pack")
    .IsDependentOn("Restore")    
    .IsDependentOn("Clean")
    .Does(() =>
{
    string versionSuffix = null;
    if (!string.IsNullOrEmpty(preReleaseSuffix))
    {
        versionSuffix = preReleaseSuffix + "-" + buildNumber.ToString("D4");
    }
    var settings = new DotNetCorePackSettings
    {
        Configuration = configuration,
        OutputDirectory = packagesDir,
        VersionSuffix = versionSuffix
    };
    
    foreach(var packDir in packDirs)
    {
        DotNetCorePack(packDir, settings);
    }    
});

Task("RunInspectCode")
	.WithCriteria(() => !skipReSharperCodeInspect)
    .Does(() =>
{
	InspectCode(solutionFile, new InspectCodeSettings { SolutionWideAnalysis = true, Profile = resharperSettings, OutputFile = inspectCodeXml });
    ReSharperReports(inspectCodeXml, inspectCodeHtml);
});

Task("RunTests")
	.WithCriteria(() => coverWith == "None" || !IsRunningOnWindows())
    .IsDependentOn("Build")	
    .Does(() =>
{
    var projects = GetFiles("./test/**/*.csproj");

    CreateDirectory(testResultsDir);
    CreateDirectory(coverageResultsDir);

    Context.Information("Found " + projects.Count() + " projects");

    foreach (var project in projects)
    {		
		var folderName = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(project.ToString())).Name;				
        var settings = new DotNetCoreTestSettings
		{
			Configuration = configuration,
			 ArgumentCustomization = args => args.Append("--logger:trx")
		};

		// Ignore Net452 on non-windows environments
		if (folderName.Contains("Net452") && !IsRunningOnWindows())
		{
			continue;
		}

		if (!IsRunningOnWindows())
        {
			settings.Framework = "netcoreapp1.1";
        }	 

		DotNetCoreTest(project.FullPath, settings);
    }
});

Task("HtmlCoverageReport")    
    .WithCriteria(() => FileExists(testCoverageOutputFilePath) && coverWith != "None" && IsRunningOnWindows())    
    .IsDependentOn("RunTests")
    .Does(() => 
{
    if (coverWith == "DotCover")
	{
		DotCoverReport(
				mergedCoverageSnapshots,
				htmlCoverageReport,
				new DotCoverReportSettings {
					ReportType = DotCoverReportType.HTML
			});
	}
	else if (coverWith == "OpenCover")
	{
		ReportGenerator(testCoverageOutputFilePath, coverageResultsDir);
	}
});

Task("RunTestsWithOpenCover")
	.WithCriteria(() => coverWith == "OpenCover" && IsRunningOnWindows())
    .IsDependentOn("Build")	
    .Does(() =>
{
	var projects = GetFiles("./test/**/*.csproj");

    CreateDirectory(testResultsDir);
    CreateDirectory(coverageResultsDir);

    Context.Information("Found " + projects.Count() + " projects");

	var settings = new DotNetCoreTestSettings
    {
        Configuration = configuration,
		 ArgumentCustomization = args => args.Append("--logger:trx")
    };

    foreach (var project in projects)
    {		
		var folderName = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(project.ToString())).Name;				
        
		Action<ICakeContext> testAction = tool => { tool.DotNetCoreTest(project.ToString(), settings); };

		var openCoverSettings = new OpenCoverSettings { 
			ReturnTargetCodeOffset = 1,
			SkipAutoProps = true,
			Register = "user",
			OldStyle = true,
			MergeOutput = true,
			ArgumentCustomization = args => args.Append(@"-safemode:off -hideskipped:All")
		};

		openCoverSettings.WithFilter(openCoverFilter);
		openCoverSettings.ExcludeByAttribute(excludeFromCoverage);
		openCoverSettings.ExcludeByFile(openCoverExcludeFile);

        OpenCover(testAction, testOCoverageOutputFilePath, openCoverSettings);					
    }
});

Task("RunTestsWithDotCover")
	.WithCriteria(() => coverWith == "DotCover" && IsRunningOnWindows())
    .IsDependentOn("Build")	
    .Does(() =>
{
	var projects = GetFiles("./test/**/*.csproj");

    CreateDirectory(testResultsDir);
    CreateDirectory(coverageResultsDir);

    Context.Information("Found " + projects.Count() + " projects");

	var settings = new DotNetCoreTestSettings
    {
        Configuration = configuration,
		 ArgumentCustomization = args => args.Append("--logger:trx")
    };

    foreach (var project in projects)
    {		
		var folderName = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(project.ToString())).Name;				
        
		Action<ICakeContext> testAction = tool => { tool.DotNetCoreTest(project.ToString(), settings); };

		var dotCoverSettings = new DotCoverCoverSettings 
		{
			ArgumentCustomization = args => args.Append(@"/HideAutoProperties")
				.Append(@"/AttributeFilters=" + excludeFromCoverage)
				.Append(@"/ReturnTargetExitCode")								
		};
					
		dotCoverSettings.WithFilter(coverIncludeFilter).WithFilter(coverExcludeFilter);

		var coverageFile = coverageResultsDir.FullPath + folderName + ".dcvr";					

		DotCoverCover(testAction, new FilePath(coverageFile), dotCoverSettings);	

		MoveFiles(coverageFile, coverageResultsDir);				
    }    

	var snapshots = GetFiles(coverageResultsDir.FullPath + "/*.dcvr");		

	if (snapshots != null && snapshots.Any()) 
	{
		DotCoverMerge(snapshots,
			new FilePath(mergedCoverageSnapshots), null);			

		DotCoverReport(
			mergedCoverageSnapshots,
			xmlCoverageReport,
			new DotCoverReportSettings { ReportType = DotCoverReportType.XML});
	}
});


Task("PublishCoverage")    
    .WithCriteria(() => FileExists(testOCoverageOutputFilePath))
    .WithCriteria(() => !BuildSystem.IsLocalBuild)
	.WithCriteria(() => coverWith == "OpenCover")
    .WithCriteria(() => !string.IsNullOrEmpty(coverallsToken))
    .IsDependentOn("RunTests")
    .Does(() => 
{
    CoverallsIo(testOCoverageOutputFilePath, new CoverallsIoSettings()
    {
        RepoToken = coverallsToken
    });
});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")	
    .IsDependentOn("Build")
	.IsDependentOn("RunTestsWithOpenCover")
	.IsDependentOn("RunTestsWithDotCover")
    .IsDependentOn("RunTests")
    .IsDependentOn("Pack")
	.IsDependentOn("HtmlCoverageReport")
	.IsDependentOn("RunInspectCode");	

Task("AppVeyor")
    .IsDependentOn("Build")
	.IsDependentOn("RunTestsWithOpenCover")
	.IsDependentOn("RunTestsWithDotCover")
    .IsDependentOn("RunTests")
    .IsDependentOn("Pack")
	.IsDependentOn("HtmlCoverageReport")
	.IsDependentOn("RunInspectCode")	
    .IsDependentOn("PublishCoverage");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);