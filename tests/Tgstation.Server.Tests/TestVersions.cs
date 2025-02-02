﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Newtonsoft.Json.Linq;

using Tgstation.Server.Api;
using Tgstation.Server.Client;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host;
using Tgstation.Server.Host.Components.Byond;
using Tgstation.Server.Host.Components.Interop;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.Database;
using Tgstation.Server.Host.IO;
using Tgstation.Server.Host.System;
using System.Net;

namespace Tgstation.Server.Tests
{
	[TestClass]
	[TestCategory("SkipWhenLiveUnitTesting")]
	public sealed class TestVersions
	{
		static XNamespace xmlNamespace;

		static XElement versionsPropertyGroup;

		[ClassInitialize]
		public static void Init(TestContext _)
		{
			var doc = XDocument.Load("../../../../../build/Version.props");
			var project = doc.Root;
			xmlNamespace = project.GetDefaultNamespace();
			versionsPropertyGroup = project.Elements().First(x => x.Name == xmlNamespace + "PropertyGroup");
			Assert.IsNotNull(versionsPropertyGroup);
		}

		[TestMethod]
		public void TestCoreVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsCoreVersion").Value + ".0";
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var actual = typeof(Program).Assembly.GetName().Version;
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestConfigVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsConfigVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var actual = GeneralConfiguration.CurrentConfigVersion;
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestApiVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsApiVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			Assert.AreEqual(expected, ApiHeaders.Version);
		}

		[TestMethod]
		public void TestApiLibraryVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsApiLibraryVersion").Value + ".0";
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var actual = typeof(ApiHeaders).Assembly.GetName().Version;
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public async Task TestDDExeByondVersion()
		{
			var mockGeneralConfigurationOptions = new Mock<IOptions<GeneralConfiguration>>();
			mockGeneralConfigurationOptions.SetupGet(x => x.Value).Returns(new GeneralConfiguration());
			var mockSessionConfigurationOptions = new Mock<IOptions<SessionConfiguration>>();
			mockSessionConfigurationOptions.SetupGet(x => x.Value).Returns(new SessionConfiguration());

			using var loggerFactory = LoggerFactory.Create(builder =>
			{
				builder.AddConsole();
				builder.SetMinimumLevel(LogLevel.Trace);
			});
			var logger = loggerFactory.CreateLogger<CachingFileDownloader>();

			// windows only BYOND but can be checked on any system
			var init1 = CachingFileDownloader.InitializeByondVersion(
				logger,
				WindowsByondInstaller.DDExeVersion,
				true,
				CancellationToken.None);
			await CachingFileDownloader.InitializeByondVersion(
				logger,
				new Version(WindowsByondInstaller.DDExeVersion.Major, WindowsByondInstaller.DDExeVersion.Minor - 1),
				true,
				CancellationToken.None);
			await init1;

			using var byondInstaller = new WindowsByondInstaller(
				Mock.Of<IProcessExecutor>(),
				Mock.Of<IIOManager>(),
				new CachingFileDownloader(Mock.Of<ILogger<CachingFileDownloader>>()),
				mockGeneralConfigurationOptions.Object,
				Mock.Of<ILogger<WindowsByondInstaller>>());

			const string ArchiveEntryPath = "byond/bin/dd.exe";
			var hasEntry = ArchiveHasFileEntry(
				await byondInstaller.DownloadVersion(WindowsByondInstaller.DDExeVersion, default),
				ArchiveEntryPath);

			Assert.IsTrue(hasEntry);

			var (byondBytes, version) = await GetByondVersionPriorTo(byondInstaller, WindowsByondInstaller.DDExeVersion);
			hasEntry = ArchiveHasFileEntry(
				byondBytes,
				ArchiveEntryPath);

			Assert.IsFalse(hasEntry);
		}

		[TestMethod]
		public async Task TestMapThreadsByondVersion()
		{
			var mockGeneralConfigurationOptions = new Mock<IOptions<GeneralConfiguration>>();
			mockGeneralConfigurationOptions.SetupGet(x => x.Value).Returns(new GeneralConfiguration
			{
				SkipAddingByondFirewallException = true,
			});
			var mockSessionConfigurationOptions = new Mock<IOptions<SessionConfiguration>>();
			mockSessionConfigurationOptions.SetupGet(x => x.Value).Returns(new SessionConfiguration());

			using var loggerFactory = LoggerFactory.Create(builder =>
			{
				builder.AddConsole();
				builder.SetMinimumLevel(LogLevel.Trace);
			});

			var platformIdentifier = new PlatformIdentifier();
			var logger = loggerFactory.CreateLogger<CachingFileDownloader>();
			var init1 = CachingFileDownloader.InitializeByondVersion(
				logger,
				ByondInstallerBase.MapThreadsVersion,
				platformIdentifier.IsWindows,
				CancellationToken.None);
			await CachingFileDownloader.InitializeByondVersion(
				logger,
				new Version(ByondInstallerBase.MapThreadsVersion.Major, ByondInstallerBase.MapThreadsVersion.Minor - 1),
				platformIdentifier.IsWindows,
				CancellationToken.None);
			await init1;

			var fileDownloader = new CachingFileDownloader(Mock.Of<ILogger<CachingFileDownloader>>());

			IByondInstaller byondInstaller = platformIdentifier.IsWindows
				? new WindowsByondInstaller(
					Mock.Of<IProcessExecutor>(),
					Mock.Of<IIOManager>(),
					fileDownloader,
					mockGeneralConfigurationOptions.Object,
					loggerFactory.CreateLogger<WindowsByondInstaller>())
				: new PosixByondInstaller(
					new PosixPostWriteHandler(loggerFactory.CreateLogger<PosixPostWriteHandler>()),
					new DefaultIOManager(),
					fileDownloader,
					loggerFactory.CreateLogger<PosixByondInstaller>());
			using var disposable = byondInstaller as IDisposable;

			var processExecutor = new ProcessExecutor(
				platformIdentifier.IsWindows
					? new WindowsProcessFeatures(Mock.Of<ILogger<WindowsProcessFeatures>>())
					: new PosixProcessFeatures(
						new Lazy<IProcessExecutor>(() => null),
						Mock.Of<IIOManager>(),
						loggerFactory.CreateLogger<PosixProcessFeatures>()),
					Mock.Of<IIOManager>(),
					loggerFactory.CreateLogger<ProcessExecutor>(),
					loggerFactory);

			var ioManager = new DefaultIOManager();
			var tempPath = Path.GetTempFileName();
			await ioManager.DeleteFile(tempPath, default);
			await ioManager.CreateDirectory(tempPath, default);
			try
			{
				await TestMapThreadsVersion(
					ByondInstallerBase.MapThreadsVersion,
					await byondInstaller.DownloadVersion(ByondInstallerBase.MapThreadsVersion, default),
					byondInstaller,
					ioManager,
					processExecutor,
					tempPath);

				await ioManager.DeleteDirectory(tempPath, default);

				var (byondBytes, version) = await GetByondVersionPriorTo(byondInstaller, ByondInstallerBase.MapThreadsVersion);

				await TestMapThreadsVersion(
					version,
					byondBytes,
					byondInstaller,
					ioManager,
					processExecutor,
					tempPath);
			}
			finally
			{
				await ioManager.DeleteDirectory(tempPath, default);
			}
		}

		[ClassCleanup]
		public static void Cleanup()
		{
			CachingFileDownloader.Cleanup();
		}

		[TestMethod]
		public void TestClientVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsClientVersion").Value + ".0";
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var actual = typeof(ServerClientFactory).Assembly.GetName().Version;
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestWatchdogVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsHostWatchdogVersion").Value + ".0";
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var actual = typeof(Host.Watchdog.WatchdogFactory).Assembly.GetName().Version;
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestDmapiVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsDmapiVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var lines = File.ReadAllLines("../../../../../src/DMAPI/tgs.dm");

			const string Prefix = "#define TGS_DMAPI_VERSION ";
			var versionLine = lines.FirstOrDefault(l => l.StartsWith(Prefix));
			Assert.IsNotNull(versionLine);

			versionLine = versionLine.Substring(Prefix.Length + 1, expected.ToString().Length);

			Assert.IsTrue(Version.TryParse(versionLine, out var actual));
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestInteropVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsInteropVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			Assert.AreEqual(expected, DMApiConstants.InteropVersion);
		}

		[TestMethod]
		public void TestControlPanelVersion()
		{
			var doc = XDocument.Load("../../../../../build/ControlPanelVersion.props");
			var project = doc.Root;
			var controlPanelXmlNamespace = project.GetDefaultNamespace();
			var controlPanelVersionsPropertyGroup = project.Elements().First(x => x.Name == controlPanelXmlNamespace + "PropertyGroup");
			var versionString = controlPanelVersionsPropertyGroup.Element(controlPanelXmlNamespace + "TgsControlPanelVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));

			var jsonText = File.ReadAllText("../../../../../src/Tgstation.Server.Host/ClientApp/package.json");

			dynamic json = JObject.Parse(jsonText);

			string cpVersionString = json.version;

			Assert.IsTrue(Version.TryParse(cpVersionString, out var actual));
			Assert.AreEqual(expected, actual);
		}

		[TestMethod]
		public void TestWatchdogClientVersion()
		{
			var expected = typeof(Host.Watchdog.WatchdogFactory).Assembly.GetName().Version;
			var actual = Program.HostWatchdogVersion;
			Assert.AreEqual(expected.Major, actual.Major);
			Assert.AreEqual(expected.Minor, actual.Minor);
			Assert.AreEqual(expected.Build, actual.Build);
			Assert.AreEqual(-1, actual.Revision);
		}

		[TestMethod]
		public async Task TestContainerScriptVersion()
		{
			var versionString = versionsPropertyGroup.Element(xmlNamespace + "TgsContainerScriptVersion").Value;
			Assert.IsNotNull(versionString);
			Assert.IsTrue(Version.TryParse(versionString, out var expected));
			var scriptLines = await File.ReadAllLinesAsync("../../../../../build/tgs.docker.sh");

			var line = scriptLines.FirstOrDefault(x => x.Trim().Contains($"SCRIPT_VERSION=\"{expected.Semver()}\""));
			Assert.IsNotNull(line);
		}

		[TestMethod]
		public void TestDowngradeMigrations()
		{
			static string GetMigrationTimestampString(Type type) => type
				?.GetCustomAttributes(typeof(MigrationAttribute), false)
				.OfType<MigrationAttribute>()
				.SingleOrDefault()
				?.Id
				.Split('_')
				.First()
				?? String.Empty;

			var allTypesWithMigrationAttributes = typeof(Program)
				.Assembly
				.GetTypes()
				.ToDictionary(
					x => x,
					x => GetMigrationTimestampString(x));

			Type latestMigrationMS = null;
			Type latestMigrationMY = null;
			Type latestMigrationPG = null;
			Type latestMigrationSL = null;
			foreach (var kvp in allTypesWithMigrationAttributes)
			{
				var migrationType = kvp.Key;
				var migrationTimestamp = kvp.Value;

				switch (migrationType.Name[..2])
				{
					case "MS":
						if (String.Compare(GetMigrationTimestampString(latestMigrationMS), migrationTimestamp) < 0)
							latestMigrationMS = migrationType;
						break;
					case "MY":
						if (String.Compare(GetMigrationTimestampString(latestMigrationMY), migrationTimestamp) < 0)
							latestMigrationMY = migrationType;
						break;
					case "PG":
						if (String.Compare(GetMigrationTimestampString(latestMigrationPG), migrationTimestamp) < 0)
							latestMigrationPG = migrationType;
						break;
					case "SL":
						if (String.Compare(GetMigrationTimestampString(latestMigrationSL), migrationTimestamp) < 0)
							latestMigrationSL = migrationType;
						break;
				}
			}

			Assert.AreEqual(latestMigrationMS, DatabaseContext.MSLatestMigration);
			Assert.AreEqual(latestMigrationMY, DatabaseContext.MYLatestMigration);
			Assert.AreEqual(latestMigrationPG, DatabaseContext.PGLatestMigration);
			Assert.AreEqual(latestMigrationSL, DatabaseContext.SLLatestMigration);
		}

		static async Task<Tuple<MemoryStream, Version>> GetByondVersionPriorTo(IByondInstaller byondInstaller, Version version)
		{
			var minusOneMinor = new Version(version.Major, version.Minor - 1);
			try
			{
				return Tuple.Create(await byondInstaller.DownloadVersion(minusOneMinor, default), minusOneMinor);
			}
			catch (HttpRequestException)
			{
				var minusOneMajor = new Version(minusOneMinor.Major - 1, minusOneMinor.Minor);
				return Tuple.Create(await byondInstaller.DownloadVersion(minusOneMajor, default), minusOneMajor);
			}
		}

		static async Task TestMapThreadsVersion(
			Version byondVersion,
			Stream byondBytes,
			IByondInstaller byondInstaller,
			IIOManager ioManager,
			IProcessExecutor processExecutor,
			string tempPath)
		{
			using (byondBytes)
				await ioManager.ZipToDirectory(tempPath, byondBytes, default);

			// HAAAAAAAX
			if (byondInstaller.GetType() == typeof(WindowsByondInstaller))
				typeof(WindowsByondInstaller).GetField("installedDirectX", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(byondInstaller, true);

			await byondInstaller.InstallByond(byondVersion, tempPath, default);

			var ddPath = ioManager.ConcatPath(
				tempPath,
				ByondManager.BinPath,
				byondInstaller.GetDreamDaemonName(byondVersion, out var supportsCli, out var shouldSupportMapThreads));

			Assert.IsTrue(supportsCli);

			await File.WriteAllBytesAsync("fake.dmb", Array.Empty<byte>(), CancellationToken.None);

			try
			{
				await using var process = processExecutor.LaunchProcess(
					ddPath,
					Environment.CurrentDirectory,
					"fake.dmb -map-threads 3 -close",
					null,
					true,
					true);

				try
				{
					await process.Startup;
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
					await process.Lifetime.WaitAsync(cts.Token);

					var output = await process.GetCombinedOutput(cts.Token);

					var supportsMapThreads = !output.Contains("invalid option '-map-threads'");
					Assert.AreEqual(shouldSupportMapThreads, supportsMapThreads, $"DD Output:{Environment.NewLine}{output}");
				}
				finally
				{
					process.Terminate();
				}
			}
			finally
			{
				File.Delete("fake.dmb");
			}
		}

		static bool ArchiveHasFileEntry(Stream byondBytes, string entryPath)
		{
			using (byondBytes)
			{
				using var archive = new ZipArchive(byondBytes, ZipArchiveMode.Read);

				var entry = archive.Entries.FirstOrDefault(entry => entry.FullName == entryPath);

				return entry != null;
			}
		}
	}
}
