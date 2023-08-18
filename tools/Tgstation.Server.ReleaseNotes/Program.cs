﻿// This program is minimal effort and should be sent to remedial school

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

using Newtonsoft.Json;

using Octokit;
using Octokit.GraphQL;

using Tgstation.Server.Host.Extensions.Converters;

using YamlDotNet.Serialization;

namespace Tgstation.Server.ReleaseNotes
{
	/// <summary>
	/// Contains the application entrypoint
	/// </summary>
	static class Program
	{
		const string RepoOwner = "tgstation";
		const string RepoName = "tgstation-server";

		/// <summary>
		/// The entrypoint for the <see cref="Program"/>
		/// </summary>
		static async Task<int> Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("Missing version argument!");
				return 1;
			}

			var versionString = args[0];
			var ensureRelease = versionString.Equals("--ensure-release", StringComparison.OrdinalIgnoreCase);
			var linkWinget = versionString.Equals("--link-winget", StringComparison.OrdinalIgnoreCase);
			var shaCheck = versionString.Equals("--winget-template-check", StringComparison.OrdinalIgnoreCase);
			var fullNotes = versionString.Equals("--generate-full-notes", StringComparison.OrdinalIgnoreCase);

			if ((!Version.TryParse(versionString, out var version) || version.Revision != -1) && !ensureRelease && !linkWinget && !shaCheck && !fullNotes)
			{
				Console.WriteLine("Invalid version: " + versionString);
				return 2;
			}

			var doNotCloseMilestone = args.Length > 1 && args[1].ToUpperInvariant() == "--NO-CLOSE";

			const string ReleaseNotesEnvVar = "TGS_RELEASE_NOTES_TOKEN";
			var githubToken = Environment.GetEnvironmentVariable(ReleaseNotesEnvVar);
			if (String.IsNullOrWhiteSpace(githubToken) && !doNotCloseMilestone && !ensureRelease)
			{
				Console.WriteLine("Missing " + ReleaseNotesEnvVar + " environment variable!");
				return 3;
			}

			var client = new GitHubClient(new Octokit.ProductHeaderValue("tgs_release_notes"));
			if (!String.IsNullOrWhiteSpace(githubToken))
			{
				client.Credentials = new Credentials(githubToken);
			}

			try
			{
				if (ensureRelease)
					return await EnsureRelease(client);

				if (linkWinget)
				{
					if (args.Length < 2 || !Uri.TryCreate(args[1], new UriCreationOptions(), out var actionsUrl))
					{
						Console.WriteLine("Missing/Invalid actions URL!");
						return 30;
					}

					return await Winget(client, actionsUrl, null);
				}

				if (shaCheck)
				{
					if(args.Length < 2)
					{
						Console.WriteLine("Missing SHA for PR template!");
						return 32;
					}

					return await Winget(client, null, args[1]);
				}

				if (fullNotes)
				{
					return await FullNotes(client);
				}

				var releasesTask = client.Repository.Release.GetAll(RepoOwner, RepoName);

				Console.WriteLine("Getting merged pull requests in milestone " + versionString + "...");
				var milestonePRs = await client.Search.SearchIssues(new SearchIssuesRequest
				{
					Milestone = $"v{versionString}",
					Type = IssueTypeQualifier.PullRequest,
					Repos = { { RepoOwner, RepoName } }
				}).ConfigureAwait(false);

				if (milestonePRs.IncompleteResults)
				{
					Console.WriteLine("Incomplete results for milestone PRs query!");
					return 5;
				}
				Console.WriteLine(milestonePRs.Items.Count + " total pull requests");

				bool postControlPanelMessage = false;

				var noteTasks = new List<Task<Tuple<Dictionary<Component, Changelist>, Dictionary<Component, Version>, bool>>>();

				foreach (var I in milestonePRs.Items)
					noteTasks.Add(GetReleaseNotesFromPR(client, I, doNotCloseMilestone, false, false));

				var releases = await releasesTask.ConfigureAwait(false);

				Version highestReleaseVersion = null;
				Release highestRelease = null;
				foreach (var I in releases)
				{
					if (!Version.TryParse(I.TagName.Replace("tgstation-server-v", String.Empty), out var currentReleaseVersion))
					{
						Console.WriteLine("WARNING: Unable to determine version of release " + I.HtmlUrl);
						continue;
					}

					if (currentReleaseVersion.Major > 3 && (highestReleaseVersion == null || currentReleaseVersion > highestReleaseVersion) && version != currentReleaseVersion)
					{
						highestReleaseVersion = currentReleaseVersion;
						highestRelease = I;
					}
				}

				if (highestReleaseVersion == null)
				{
					Console.WriteLine("Unable to determine highest release version!");
					return 6;
				}

				var oldNotes = highestRelease.Body;

				var splits = new List<string>(oldNotes.Split('\n'));
				//trim away all the lines that don't start with #

				string keepThisRelease;
				if (version.Build <= 1)
					keepThisRelease = "# ";
				else
					keepThisRelease = "## ";

				for (; !splits[0].StartsWith(keepThisRelease, StringComparison.Ordinal); splits.RemoveAt(0))
					if (splits.Count == 1)
					{
						Console.WriteLine("Error formatting release notes: Can't detemine notes start!");
						return 7;
					}

				oldNotes = String.Join('\n', splits);

				string prefix;
				const string PropsPath = "build/Version.props";
				const string ControlPanelPropsPath = "build/ControlPanelVersion.props";

				var doc = XDocument.Load(PropsPath);
				var project = doc.Root;
				var xmlNamespace = project.GetDefaultNamespace();
				var versionsPropertyGroup = project.Elements().First(x => x.Name == xmlNamespace + "PropertyGroup");

				var doc2 = XDocument.Load(ControlPanelPropsPath);
				var project2 = doc2.Root;
				var controlPanelXmlNamespace = project2.GetDefaultNamespace();
				var controlPanelVersionsPropertyGroup = project2.Elements().First(x => x.Name == controlPanelXmlNamespace + "PropertyGroup");

				var coreVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsCoreVersion").Value);
				if (coreVersion != version)
				{
					Console.WriteLine("Received a different version on command line than in Version.props!");
					return 10;
				}

				var apiVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsApiVersion").Value);
				var configVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsConfigVersion").Value);
				var dmApiVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsDmapiVersion").Value);
				var interopVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsInteropVersion").Value);
				var webControlVersion = Version.Parse(controlPanelVersionsPropertyGroup.Element(controlPanelXmlNamespace + "TgsControlPanelVersion").Value);
				var hostWatchdogVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsHostWatchdogVersion").Value);

				if (webControlVersion.Major == 0)
					postControlPanelMessage = true;

				prefix = $"Please refer to the [README](https://github.com/tgstation/tgstation-server#setup) for setup instructions. Full changelog can be found [here](https://raw.githubusercontent.com/tgstation/tgstation-server/gh-pages/changelog.yml).{Environment.NewLine}{Environment.NewLine}#### Component Versions\nCore: {coreVersion}\nConfiguration: {configVersion}\nHTTP API: {apiVersion}\nDreamMaker API: {dmApiVersion} (Interop: {interopVersion})\n[Web Control Panel](https://github.com/tgstation/tgstation-server-webpanel): {webControlVersion}\nHost Watchdog: {hostWatchdogVersion}";

				var newNotes = new StringBuilder(prefix);
				if (postControlPanelMessage)
				{
					newNotes.Append(Environment.NewLine);
					newNotes.Append(Environment.NewLine);
					newNotes.Append("### The recommended client is currently the legacy [Tgstation.Server.ControlPanel](https://github.com/tgstation/Tgstation.Server.ControlPanel/releases/latest). This will be phased out as the web client is completed.");
				}

				newNotes.Append(Environment.NewLine);
				newNotes.Append(Environment.NewLine);
				if (version.Build == 0)
				{
					newNotes.Append("# [Update ");
					newNotes.Append(version.Minor);
					newNotes.Append(".X");
				}
				else
				{
					newNotes.Append("## [Patch ");
					newNotes.Append(version.Build);
				}
				newNotes.Append("](");

				var milestone = await milestoneTasks.Single().Value.ConfigureAwait(false);
				if (milestone == null)
				{
					Console.WriteLine("Unable to detemine milestone!");
					return 9;
				}

				var allTasks = new List<Task>(noteTasks);
				if (doNotCloseMilestone)
					Console.WriteLine("Not closing milestone due to parameter!");
				else
				{
					Console.WriteLine("Closing milestone...");
					allTasks.Add(client.Issue.Milestone.Update(RepoOwner, RepoName, milestone.Number, new MilestoneUpdate
					{
						State = ItemState.Closed
					}));

					// Create the next patch milestone
					var nextPatchMilestoneName = $"v{version.Major}.{version.Minor}.{version.Build + 1}";
					Console.WriteLine($"Creating milestone {nextPatchMilestoneName}...");
					var nextPatchMilestone = await client.Issue.Milestone.Create(
						RepoOwner,
						RepoName,
						new NewMilestone(nextPatchMilestoneName)
						{
							Description = "Next patch version"
						});

					if (version.Build == 0)
					{
						// close the patch milestone if it exists
						var milestones = await client.Issue.Milestone.GetAllForRepository(RepoOwner, RepoName, new MilestoneRequest
						{
							State = ItemStateFilter.Open
						});

						var milestoneToDelete = milestones.FirstOrDefault(x => x.Title.StartsWith($"v{highestReleaseVersion.Major}.{highestReleaseVersion.Minor}."));
						if (milestoneToDelete != null)
						{
							Console.WriteLine($"Moving {milestoneToDelete.OpenIssues} open issues and {milestoneToDelete.ClosedIssues} closed issues from unused patch milestone {milestoneToDelete.Title} to upcoming ones and deleting...");
							if (milestoneToDelete.OpenIssues + milestoneToDelete.ClosedIssues > 0)
							{
								var issuesInUnusedMilestone = await client.Search.SearchIssues(new SearchIssuesRequest
								{
									Milestone = milestoneToDelete.Title,
									Repos = { { RepoOwner, RepoName } }
								});

								var issueUpdateTasks = new List<Task>();
								foreach (var I in issuesInUnusedMilestone.Items)
								{
									issueUpdateTasks.Add(client.Issue.Update(RepoOwner, RepoName, I.Number, new IssueUpdate
									{
										Milestone = I.State.Value == ItemState.Closed ? milestone.Number : nextPatchMilestone.Number
									}));

									if (I.PullRequest != null)
									{
										Console.WriteLine($"Adding additional merged PR #{I.Number}...");
										var task = GetReleaseNotesFromPR(client, I, doNotCloseMilestone, false, false);
										noteTasks.Add(task);
										allTasks.Add(task);
									}
								}

								await Task.WhenAll(issueUpdateTasks).ConfigureAwait(false);
							}

							allTasks.Add(client.Issue.Milestone.Delete(RepoOwner, RepoName, milestoneToDelete.Number));
						}

						// Create the next minor milestone
						var nextMinorMilestoneName = $"v{version.Major}.{version.Minor + 1}.0";
						Console.WriteLine($"Creating milestone {nextMinorMilestoneName}...");
						var nextMinorMilestoneTask = client.Issue.Milestone.Create(
							RepoOwner,
							RepoName,
							new NewMilestone(nextMinorMilestoneName)
							{
								Description = "Next minor version"
							});
						allTasks.Add(nextMinorMilestoneTask);

						// Move unfinished stuff to new minor milestone
						Console.WriteLine($"Moving {milestone.OpenIssues} abandoned issue(s) from previous milestone to new one...");
						var abandonedIssues = await client.Search.SearchIssues(new SearchIssuesRequest
						{
							Milestone = milestone.Title,
							Repos = { { RepoOwner, RepoName } },
							State = ItemState.Open
						});

						if (abandonedIssues.Items.Any())
						{
							var nextMinorMilestone = await nextMinorMilestoneTask.ConfigureAwait(false);
							foreach (var I in abandonedIssues.Items)
								allTasks.Add(client.Issue.Update(RepoOwner, RepoName, I.Number, new IssueUpdate
								{
									Milestone = nextMinorMilestone.Number
								}));
						}
					}
				}

				newNotes.Append(milestone.HtmlUrl);
				newNotes.Append("?closed=1)");
				newNotes.Append(Environment.NewLine);

				await Task.WhenAll(allTasks).ConfigureAwait(false);

				var componentVersionDict = new Dictionary<Component, Version>
				{
					{ Component.Configuration, configVersion },
					{ Component.HttpApi, apiVersion },
					{ Component.DreamMakerApi, dmApiVersion },
					{ Component.InteropApi, interopVersion },
					{ Component.WebControlPanel, webControlVersion },
					{ Component.HostWatchdog, hostWatchdogVersion },
				};

				var releaseDictionary = new Dictionary<Component, Changelist>(
					noteTasks
						.SelectMany(task => task.Result.Item1)
						.GroupBy(kvp => kvp.Key)
						.Select(grouping =>
						{
							var component = grouping.Key;
							var changelist = new Changelist
							{
								Changes = grouping.SelectMany(kvp => kvp.Value.Changes).ToList()
							};

							if (component == Component.Core)
							{
								changelist.Version = coreVersion;
								changelist.ComponentVersions = componentVersionDict;
							}
							else
								changelist.Version = componentVersionDict[component];

							return new KeyValuePair<Component, Changelist>(component, changelist);
						}));

				if (releaseDictionary.Count == 0)
				{
					Console.WriteLine("No release notes for this milestone!");
					return 8;
				}

				foreach (var I in releaseDictionary.OrderBy(kvp => kvp.Key))
				{
					newNotes.Append(Environment.NewLine);
					newNotes.Append("#### ");
					string componentName = I.Key switch
					{
						Component.HttpApi => "HTTP API",
						Component.InteropApi => "Interop API",
						Component.Configuration => "**Configuration**",
						Component.DreamMakerApi => "DreamMaker API",
						Component.HostWatchdog => "Host Watchdog",
						Component.Core => "Core",
						Component.WebControlPanel => "Web Control Panel",
						_ => throw new Exception($"Unknown Component: {I.Key}"),
					};
					newNotes.Append(componentName);

					foreach (var change in I.Value.Changes)
						foreach (var line in change.Descriptions)
						{
							newNotes.Append(Environment.NewLine);
							newNotes.Append("- ");
							newNotes.Append(line);
							newNotes.Append(" (#");
							newNotes.Append(change.PullRequest);
							newNotes.Append(" @");
							newNotes.Append(change.Author);
							newNotes.Append(')');
						}

					newNotes.Append(Environment.NewLine);
				}

				newNotes.Append(Environment.NewLine);

				if (version.Minor != 0 && version.Build != 0)
					newNotes.Append(oldNotes);

				const string OutputPath = "release_notes.md";
				Console.WriteLine($"Writing out new release notes to {Path.GetFullPath(OutputPath)}...");
				var releaseNotes = newNotes.ToString();
				await File.WriteAllTextAsync(OutputPath, releaseNotes).ConfigureAwait(false);

				Console.WriteLine("Updating Server Release Thread...");
				var productInformation = new Octokit.GraphQL.ProductHeaderValue("tgs_release_notes");
				var connection = new Octokit.GraphQL.Connection(productInformation, githubToken);

				var mutation = new Mutation()
					.AddDiscussionComment(new Octokit.GraphQL.Model.AddDiscussionCommentInput
					{
						Body = $"[tgstation-server-v{versionString}](https://github.com/tgstation/tgstation-server/releases/tag/tgstation-server-v{versionString}) released.",
						DiscussionId = new ID("MDEwOkRpc2N1c3Npb24zNTU5OTUx")
					})
					.Select(payload => new
					{
						payload.ClientMutationId
					})
					.Compile();

				if (!doNotCloseMilestone)
					await connection.Run(mutation);

				return 0;
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
				return 4;
			}
		}

		static ConcurrentDictionary<int, Task<Milestone>> milestoneTasks = new ConcurrentDictionary<int, Task<Milestone>>();
		static Task<Milestone> GetMilestone(IGitHubClient client, int number)
			=> milestoneTasks.GetOrAdd(number, localNumber => client.Issue.Milestone.Get(RepoOwner, RepoName, localNumber));

		static async Task<Tuple<Dictionary<Component, Changelist>, Dictionary<Component, Version>, bool>> GetReleaseNotesFromPR(IGitHubClient client, Issue pullRequest, bool doNotCloseMilestone, bool needComponentExactVersions, bool forAllComponents)
		{
			//need to check it was merged
			var fullPR = await RLR(() => client.Repository.PullRequest.Get(RepoOwner, RepoName, pullRequest.Number));

			if (!fullPR.Merged)
			{
				if (!doNotCloseMilestone && fullPR.Milestone != null)
				{
					Console.WriteLine($"Removing trash PR #{fullPR.Number} from milestone...");
					await RLR(() => client.Issue.Update(RepoOwner, RepoName, fullPR.Number, new IssueUpdate
					{
						Milestone = null
					}));
				}

				return null;
			}

			if (fullPR.Milestone == null)
			{
				return null;
			}

			var commentsTask = TripleCheckGitHubPagination(apiOptions => client.Issue.Comment.GetAllForIssue(fullPR.Base.Repository.Id, pullRequest.Number, apiOptions), comment => comment.Id);

			bool isReleasePR = false;
			async Task<bool> ShouldGetExtendedComponentVersions()
			{
				if (forAllComponents)
					return true;

				var commit = await RLR(() => client.Repository.Commit.Get(fullPR.Base.Repository.Id, fullPR.MergeCommitSha));

				return isReleasePR = commit.Commit.Message.Contains("[TGSDeploy]");
			}

			Task<bool> needExtendedComponentVersions = null;
			async Task<Dictionary<Component, Version>> GetComponentVersions()
			{
				var mergeCommit = fullPR.MergeCommitSha;
				// we don't care about unreleased web control panel changes

				try
				{
					needExtendedComponentVersions = ShouldGetExtendedComponentVersions();

					var versionsBytes = await RLR(() => client.Repository.Content.GetRawContentByRef(RepoOwner, RepoName, "build/Version.props", mergeCommit));

					XDocument doc;
					using (var ms = new MemoryStream(versionsBytes))
						doc = XDocument.Load(ms);

					var project = doc.Root;
					var xmlNamespace = project.GetDefaultNamespace();
					var versionsPropertyGroup = project.Elements().First(x => x.Name == xmlNamespace + "PropertyGroup");

					Version Parse(string elemName, bool controlPanel = false)
					{
						var element = versionsPropertyGroup.Element(xmlNamespace + elemName);
						if (element == null)
							return null;

						return Version.Parse(element.Value);
					}

					var dict = new Dictionary<Component, Version>
					{
						{ Component.Core, Parse("TgsCoreVersion") },
						{ Component.HttpApi, Parse("TgsApiVersion") },
						{ Component.DreamMakerApi, Parse("TgsDmapiVersion") },
					};

					if (await needExtendedComponentVersions)
					{
						// only grab some versions at release time
						// we aggregate later
						dict.Add(Component.Configuration, Parse("TgsConfigVersion"));
						dict.Add(Component.InteropApi, Parse("TgsInteropVersion"));
						dict.Add(Component.HostWatchdog, Parse("TgsHostWatchdogVersion"));
						dict.Add(Component.NugetCommon, Parse("TgsCommonLibraryVersion"));
						dict.Add(Component.NugetApi, Parse("TgsApiLibraryVersion"));
						dict.Add(Component.NugetClient, Parse("TgsClientVersion"));

						var webVersion = Parse("TgsControlPanelVersion");
						if (webVersion != null)
						{
							dict.Add(Component.WebControlPanel, webVersion);
						}
						else
						{
							var controlPanelVersionBytes = await RLR(() => client.Repository.Content.GetRawContentByRef(RepoOwner, RepoName, "build/ControlPanelVersion.props", mergeCommit));
							using (var ms = new MemoryStream(controlPanelVersionBytes))
								doc = XDocument.Load(ms);


							project = doc.Root;
							var controlPanelXmlNamespace = project.GetDefaultNamespace();
							var controlPanelVersionsPropertyGroup = project.Elements().First(x => x.Name == controlPanelXmlNamespace + "PropertyGroup");
							dict.Add(Component.WebControlPanel, Version.Parse(controlPanelVersionsPropertyGroup.Element(controlPanelXmlNamespace + "TgsControlPanelVersion").Value));
						}
					}

					return dict;
				}
				catch
				{
					return new Dictionary<Component, Version>();
				}
			}

			var componentVersions = needComponentExactVersions ? GetComponentVersions() : Task.FromResult<Dictionary<Component, Version>>(null);
			var changelists = new ConcurrentDictionary<Component, Changelist>();
			async Task BuildNotesFromComment(string comment, User user, Task localPreviousTask)
			{
				await localPreviousTask;
				if (comment == null)
					return;

				async Task CommitNotes(Component component, List<string> notes)
				{
					foreach (var I in notes)
						Console.WriteLine(component + " #" + fullPR.Number + " - " + I + " (@" + user.Login + ")");

					var tupleSelector = notes.Select(note => new Change
					{
						Descriptions = new List<string> { note },
						PullRequest = fullPR.Number,
						Author = user.Login
					});

					var useExtendedComponentVersions = await needExtendedComponentVersions;
					var componentVersionsResult = await componentVersions;
					lock (changelists)
						if (changelists.TryGetValue(component, out var currentChangelist))
							currentChangelist.Changes.AddRange(tupleSelector);
						else
							Debug.Assert(changelists.TryAdd(component, new Changelist
							{
								Changes = tupleSelector.ToList(),
								Unreleased = false,
								Version = needComponentExactVersions && componentVersionsResult.TryGetValue(component, out var componentVersion)
									? componentVersion
									: null,
								ComponentVersions = component == Component.Core && needComponentExactVersions && useExtendedComponentVersions
									? new Dictionary<Component, Version>(componentVersionsResult.Where(kvp => kvp.Key != Component.Core))
									: null
							}));
				}

				var commentSplits = comment.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				string targetComponent = null;
				var notes = new List<string>();
				foreach (var line in commentSplits)
				{
					var trimmedLine = line.Trim();
					if (targetComponent == null)
					{
						if (trimmedLine.StartsWith(":cl:", StringComparison.Ordinal))
						{
							targetComponent = trimmedLine[4..].Trim();
							if (targetComponent.Length == 0)
								targetComponent = "Core";
						}
						continue;
					}
					if (trimmedLine.StartsWith("/:cl:", StringComparison.Ordinal))
					{
						if(!Enum.TryParse<Component>(targetComponent, out var component))
							switch (targetComponent.ToUpperInvariant())
							{
								case "**CONFIGURATION**":
								case "CONFIGURATION":
								case "CONFIG":
									component = Component.Configuration;
									break;
								case "HTTP API":
									component = Component.HttpApi;
									break;
								case "WEB CONTROL PANEL":
									component = Component.WebControlPanel;
									break;
								case "DMAPI":
								case "DREAMMAKER API":
									component = Component.DreamMakerApi;
									break;
								case "INTEROP API":
									component = Component.InteropApi;
									break;
								case "HOST WATCHDOG":
									component = Component.HostWatchdog;
									break;
								case "NUGET: API":
									component = Component.NugetApi;
									break;
								case "NUGET: COMMON":
									component = Component.NugetCommon;
									break;
								case "NUGET: CLIENT":
									component = Component.NugetClient;
									break;
								default:
									throw new Exception($"Unknown component: \"{targetComponent}\"");
							}

						await CommitNotes(component, notes);
						targetComponent = null;
						notes.Clear();
						continue;
					}
					if (trimmedLine.Length == 0)
						continue;

					notes.Add(trimmedLine);
				}
			}

			var previousTask = BuildNotesFromComment(fullPR.Body, fullPR.User, Task.CompletedTask);
			var comments = await commentsTask;
			foreach (var x in comments)
				previousTask = BuildNotesFromComment(x.Body, x.User, previousTask);

			await previousTask;

			return Tuple.Create(changelists.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), await componentVersions, isReleasePR);
		}

		class ExtendedReleaseUpdate : ReleaseUpdate
		{
			public bool? MakeLatest { get; set; }
		}

		static async Task<int> EnsureRelease(IGitHubClient client)
		{
			Console.WriteLine("Ensuring latest release is a GitHub release...");
			var latestRelease = await client.Repository.Release.GetLatest(RepoOwner, RepoName);

			const string TagPrefix = "tgstation-server-v";
			static bool IsServerRelease(Release release) => release.TagName.StartsWith(TagPrefix);

			if (!IsServerRelease(latestRelease))
			{
				var allReleases = await client.Repository.Release.GetAll(RepoOwner, RepoName);
				var orderedReleases = allReleases
					.Where(IsServerRelease)
					.OrderByDescending(x => Version.Parse(x.TagName[TagPrefix.Length..]));
				latestRelease = orderedReleases
					.First();

				// this should set it as latest
				await client.Repository.Release.Edit(RepoOwner, RepoName, latestRelease.Id, new ExtendedReleaseUpdate
				{
					MakeLatest = true
				});
			}

			return 0;
		}

		static async Task<int> Winget(IGitHubClient client, Uri actionUrl, string expectedTemplateSha)
		{
			const string PropsPath = "build/Version.props";

			var doc = XDocument.Load(PropsPath);
			var project = doc.Root;
			var xmlNamespace = project.GetDefaultNamespace();
			var versionsPropertyGroup = project.Elements().First(x => x.Name == xmlNamespace + "PropertyGroup");
			var coreVersion = Version.Parse(versionsPropertyGroup.Element(xmlNamespace + "TgsCoreVersion").Value);

			const string BodyForPRSha = "184dccf9de3e3e4abe289a46648af42017ad6f09";
			var prBody = $@"# Automated Pull Request

This pull request was generated by our [deployment pipeline]({actionUrl}) as a result of the release of [tgstation-server-v{coreVersion}](https://github.com/tgstation/tgstation-server/releases/tag/tgstation-server-v{coreVersion}). Validation was performed as part of the process.

The user account that created this pull request is available to correct any issues.

**_We would like to be verified as the publisher of this software but we cannot find documentation on how to do so._**

- [x] Have you signed the [Contributor License Agreement](https://cla.opensource.microsoft.com/microsoft/winget-pkgs)?
- [x] Have you checked that there aren't other open [pull requests](https://github.com/microsoft/winget-pkgs/pulls) for the same manifest update/change?
  - This PR is generated as a direct result of a new release of `tgstation-server` this should be impossible
- [x] This PR only modifies one (1) manifest
- [x] Have you [validated](https://github.com/microsoft/winget-pkgs/blob/master/AUTHORING_MANIFESTS.md#validation) your manifest locally with `winget validate --manifest <path>`?
  - Validation is performed as a prerequisite to deployment.
- [x] Have you tested your manifest locally with `winget install --manifest <path>`?
  - Manifest installation and uninstallation is performed as a prerequisite to deployment.
- [x] Does your manifest conform to the [1.5 schema](https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema/1.5.0)?";

			if (expectedTemplateSha != null)
			{
				if (expectedTemplateSha != BodyForPRSha)
				{
					Console.WriteLine("winget-pkgs pull request template has updated. This tool will need to be updated to match!");
					Console.WriteLine($"Expected {BodyForPRSha} found {expectedTemplateSha}");
					return 33;
				}

				return 0;
			}

			var clientUser = await client.User.Current();

			var userPrsOnWingetRepo = await client.Search.SearchIssues(new SearchIssuesRequest
			{
				Author = clientUser.Login,
				Is = new List<IssueIsQualifier> { IssueIsQualifier.PullRequest },
				State = ItemState.Open,
				Repos = new RepositoryCollection
				{
					{ "microsoft", "winget-pkgs" },
				},
			});

			var prToModify = userPrsOnWingetRepo.Items.OrderByDescending(pr => pr.Number).FirstOrDefault();
			if(prToModify == null)
			{
				Console.WriteLine("Could not find open winget-pkgs PR!");
				return 31;
			}

			await client.Issue.Update("microsoft", "winget-pkgs", prToModify.Number, new IssueUpdate
			{
				Body = prBody,
			});
			return 0;
		}

		static async Task<T> RLR<T>(Func<Task<T>> func)
		{
			while (true)
				try
				{
					return await func();
				}
				catch (HttpRequestException ex) when (ex.InnerException is IOException ioEx && ioEx.InnerException is SocketException sockEx && sockEx.ErrorCode == 10053)
				{
					await Task.Delay(15000);
				}
				catch (SecondaryRateLimitExceededException)
				{
					await Task.Delay(15000);
				}
				catch (RateLimitExceededException ex)
				{
					var now = DateTimeOffset.UtcNow.AddSeconds(-10);
					if (ex.Reset > now)
					{
						var delay = ex.Reset - now;
						await Task.Delay(delay);
					}
				}
		}

		static async Task<List<T>> TripleCheckGitHubPagination<T>(Func<ApiOptions, Task<IReadOnlyList<T>>> apiCall, Func<T, long> idSelector)
		{
			// I've seen GitHub pagination return incomplete result sets in the past
			// It has an in-built pagination limit of 100
			var apiOptions = new ApiOptions
			{
				PageSize = 100
			};
			var results = await RLR(() => apiCall(apiOptions));
			Dictionary<long, T> distinctEntries = new Dictionary<long, T>(results.Count);
			foreach (var result in results)
				distinctEntries.Add(idSelector(result), result);

			if (results.Count > 100)
			{
				results = await RLR(() => apiCall(apiOptions));
				foreach (var result in results)
					distinctEntries.TryAdd(idSelector(result), result);

				results = await RLR(() => apiCall(apiOptions));
				foreach (var result in results)
					distinctEntries.TryAdd(idSelector(result), result);
			}

			return distinctEntries.Values.ToList();
		}

		static async Task<Task<ReleaseNotes>> ProcessMilestone(IGitHubClient client, Milestone milestone)
		{
			// have to trust this works
			SearchIssuesResult results;

			var milestoneTask = Task.FromResult(milestone);
			var pullRequests = new Dictionary<int, Issue>();
			var iteration = 0;
			while (true)
			{
				results = await RLR(() => client.Search.SearchIssues(new SearchIssuesRequest
				{
					Type = IssueTypeQualifier.PullRequest,
					Milestone = milestone.Title,
					Repos = new RepositoryCollection
					{
						{ RepoOwner, RepoName },
					},
				}));

				foreach (var result in results.Items)
					if (pullRequests.TryAdd(result.Number, result))
						milestoneTasks.TryAdd(result.Number, milestoneTask);

				if (results.IncompleteResults)
					continue;

				if (results.TotalCount <= 100 || ++iteration == 3)
					break;
			}

			async Task<ReleaseNotes> RunPRs()
			{
				var milestoneVersion = Version.Parse(milestone.Title[1..]);
				var prTasks = pullRequests.Select(
					kvp => GetReleaseNotesFromPR(client, kvp.Value, true, true, milestone.State.Value == ItemState.Open))
					.ToList();

				await Task.WhenAll(prTasks);

				var prResults = prTasks.Select(x => x.Result).Where(result => result != null).ToList();

				var releasePRResult = prResults.FirstOrDefault(x => x.Item3);

				Dictionary<Component, Version> releasedComponentVersions;
				if (releasePRResult != null)
					releasedComponentVersions = releasePRResult.Item2;
				else
				{
					releasedComponentVersions = new Dictionary<Component, Version>(
						prResults
							.SelectMany(result => result.Item2)
							.GroupBy(kvp => kvp.Key)
							.Select(grouping => new KeyValuePair<Component, Version>(grouping.Key, grouping.Max(kvp => kvp.Value))));

					foreach(var maxVersionKvp in prResults.SelectMany(x => x.Item1)
						.Where(x => !releasedComponentVersions.ContainsKey(x.Key))
						.GroupBy(x => x.Key)
						.Select(group => {
							var versions = group
								.Where(x => x.Value.Version != null)
								.ToList();

							if (versions.Count == 0)
								return new KeyValuePair<Component, Version>(group.Key, null);

							return new KeyValuePair<Component, Version>(group.Key, versions.Max(x => x.Value.Version));
						})
						.Where(kvp => kvp.Value != null)
						.ToList())
					{
						releasedComponentVersions.Add(maxVersionKvp.Key, maxVersionKvp.Value);
					}
				}

				var finalResults = new Dictionary<Component, List<Changelist>>();
				foreach (var componentKvp in releasedComponentVersions)
				{
					var component = componentKvp.Key;
					var list = new List<Changelist>();

					foreach(var changelistDict in prResults.Select(x => x.Item1))
					{
						if (!changelistDict.TryGetValue(component, out var changelist))
							continue;

						Version componentVersion = milestoneVersion;
						var unreleased = milestone.State.Value == ItemState.Open;
						if (component != Component.Core)
						{
							componentVersion = changelist.Version ?? componentKvp.Value;
							if (releasedNonCoreVersions != null
								&& releasedNonCoreVersions.TryGetValue(component, out var releasedVersions)
								&& !releasedVersions.Any(x => x == componentVersion))
							{
								// roll forward
								var newList = releasedVersions
									.ToList();
								newList.Add(componentVersion);
								newList = newList.OrderBy(x => x).ToList();

								var index = newList.IndexOf(componentVersion);
								Debug.Assert(index != -1);
								if (index != (newList.Count - 1))
								{
									componentVersion = newList[index + 1];
									unreleased = false;
								}
								else
									unreleased = true;
							}
						}

						var entry = list.FirstOrDefault(x => x.Version == componentVersion);
						if (entry == null)
						{
							entry = changelist;
							entry.Version = componentVersion;
							entry.Unreleased = unreleased;
							list.Add(entry);
						}
						else
							entry.Changes.AddRange(changelist.Changes);
					}

					Debug.Assert(list.Select(x => x.Version.ToString()).Distinct().Count() == list.Count);
					if (component == Component.Core)
					{
						Debug.Assert(list.All(x => x.Version == milestoneVersion));
					}

					list = list.OrderByDescending(x => x.Version).ToList();
					finalResults.Add(component, list);
				}

				if (!finalResults.ContainsKey(Component.Core) || finalResults[Component.Core].Count == 0)
				{
					finalResults.Remove(Component.Core);
					finalResults.Add(Component.Core, new List<Changelist>
					{
						new Changelist
						{
							Changes = new List<Change>(),
							ComponentVersions = releasedComponentVersions,
							Unreleased = milestone.State.Value == ItemState.Open,
							Version = milestoneVersion,
						}
					});
				}
				else
					Debug.Assert(finalResults[Component.Core].All(x => x.Version == milestoneVersion));

				var notes = new ReleaseNotes
				{
					Components = new SortedDictionary<Component, List<Changelist>>(finalResults),
				};

				return notes;
			}

			return RunPRs();
		}

		static async Task<int> FullNotes(IGitHubClient client)
		{
			var startRateLimit = (client.GetLastApiInfo()?.RateLimit ?? (await client.RateLimit.GetRateLimits()).Rate).Remaining;

			ReleaseNotes existingNotes = null;
			if (File.Exists("changelog.yml"))
			{
				var existingYml = await File.ReadAllTextAsync("changelog.yml");
				var deserializer = new DeserializerBuilder()
					.Build();

				existingNotes = deserializer.Deserialize<ReleaseNotes>(existingYml);
			}

			var releaseNotes = await GenerateNotes(client, existingNotes);

			Console.WriteLine($"Generating all release notes took {startRateLimit - client.GetLastApiInfo().RateLimit.Remaining} requests.");

			var serializer = new SerializerBuilder()
				.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
				.WithTypeConverter(new VersionConverter())
				.Build();

			var serializedYaml = serializer.Serialize(releaseNotes);
			await File.WriteAllTextAsync("changelog.yml", serializedYaml).ConfigureAwait(false);
			return 0;
		}

		static HttpClient httpClient = new HttpClient(new HttpClientHandler()
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
		});
		static async Task<HashSet<Version>> EnumerateNugetVersions(string package)
		{
			var url = new Uri($"https://api.nuget.org/v3/registration5-gz-semver2/{package.ToLowerInvariant()}/index.json");

			using var req = new HttpRequestMessage();
			req.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Tgstation.Server.ReleaseNotes", "0.1.0"));
			req.Method = HttpMethod.Get;
			req.RequestUri = url;

			using var resp = await httpClient.SendAsync(req);
			resp.EnsureSuccessStatusCode();

			var json = await resp.Content.ReadAsStringAsync();

			dynamic dynamicJson = JsonConvert.DeserializeObject(json);

			var versions = (IEnumerable<dynamic>)dynamicJson.items[0].items;
			var results = versions
				.Select(x => Version.TryParse((string)x.catalogEntry.version, out var version) ? version : null)
				.Where(version => version != null)
				.OrderBy(x => x)
				.ToHashSet();
			return results;
		}

		static IReadOnlyDictionary<Component, IReadOnlySet<Version>> releasedNonCoreVersions;

		static async Task<ReleaseNotes> GenerateNotes(IGitHubClient client, ReleaseNotes previousNotes)
		{
			var releasesTask = TripleCheckGitHubPagination(
				apiOptions => client.Repository.Release.GetAll(RepoOwner, RepoName, apiOptions),
				release => release.Id);

			var milestones = await TripleCheckGitHubPagination(
				apiOptions => client.Issue.Milestone.GetAllForRepository(RepoOwner, RepoName, new MilestoneRequest {
					State = ItemStateFilter.All
				}, apiOptions),
				milestone => milestone.Id);

			var versionMilestones = milestones
				.Where(milestone => Regex.IsMatch(milestone.Title, @"v[1-9][0-9]*\.[1-9]*[0-9]+\.[1-9]*[0-9]+$"))
				.ToList();

			var releases = await releasesTask;

			var nugetCommonVersions = EnumerateNugetVersions("Tgstation.Server.Common");
			var nugetApiVersions = EnumerateNugetVersions("Tgstation.Server.Api");
			var nugetClientVersions = EnumerateNugetVersions("Tgstation.Server.Client");

			releasedNonCoreVersions = new Dictionary<Component, IReadOnlySet<Version>> {
				{ Component.HttpApi, releases
					.Where(x => x.TagName.StartsWith("api-v"))
					.Select(x => Version.Parse(x.TagName[5..]))
					.OrderBy(x => x)
					.ToHashSet() },
				{ Component.DreamMakerApi, releases
					.Where(x => x.TagName.StartsWith("dmapi-v"))
					.Select(x => Version.Parse(x.TagName[7..]))
					.OrderBy(x => x)
					.ToHashSet() },
				{ Component.NugetCommon, await nugetCommonVersions },
				{ Component.NugetApi, await nugetApiVersions },
				{ Component.NugetClient, await nugetClientVersions }
			};

			var milestonesToProcess = versionMilestones;
			if (previousNotes != null)
			{
				var releasedVersions = previousNotes.Components[Component.Core].Where(cl => !cl.Unreleased).ToList();
				milestonesToProcess = milestonesToProcess
					.Where(x => !releasedVersions.Any(
						version => version.Version == Version.Parse(x.Title.AsSpan(1))))
					.ToList();

				foreach (var kvp in previousNotes.Components)
					if (releasedNonCoreVersions.TryGetValue(kvp.Key, out var releasedComponentVersions))
						kvp.Value.RemoveAll(x => x.Unreleased = !releasedComponentVersions.Any(y => y == x.Version));
					else
						kvp.Value.RemoveAll(x => x.Unreleased);
			}

			var milestonePRTasks = milestonesToProcess
				.Select(milestone => ProcessMilestone(client, milestone))
				.ToList();

			await Task.WhenAll(milestonePRTasks);

			await Task.WhenAll(milestonePRTasks.Select(task => task.Result));

			var coreCls = milestonePRTasks
				.SelectMany(task => task.Result.Result.Components)
				.Where(x => x.Key == Component.Core)
				.ToList();

			Debug.Assert(
				coreCls.Count == milestonesToProcess.Count);

			var distinctCoreVersions = coreCls
				.SelectMany(x => x.Value)
				.Select(x => x.Version.ToString())
				.Distinct()
				.Select(Version.Parse)
				.OrderBy(x => x)
				.ToList();

			var missingCoreVersions = milestonesToProcess
				.Where(x => !distinctCoreVersions.Any(y => Version.Parse(x.Title.Substring(1)) == y))
				.ToList();

			Debug.Assert(missingCoreVersions.Count == 0);
			foreach (var missingCoreVersion in missingCoreVersions)
				await await ProcessMilestone(client, missingCoreVersion);

			var changelistsGroupedByComponent =
					milestonePRTasks
						.SelectMany(task => task.Result.Result.Components)
						.GroupBy(kvp => kvp.Key)
						.ToDictionary(grouping => grouping.Key, grouping => grouping.SelectMany(kvp => kvp.Value));

			var releaseNotes = new ReleaseNotes
			{
				Components = new SortedDictionary<Component, List<Changelist>>(
					changelistsGroupedByComponent
						.ToDictionary(
							kvp => kvp.Key,
							kvp => kvp
								.Value
								.GroupBy(changelist => changelist.Version)
								.Select(grouping =>
								{
									var firstEntry = grouping.First();
									return new Changelist
									{
										Changes = grouping.SelectMany(cl => cl.Changes).ToList(),
										ComponentVersions = firstEntry.ComponentVersions,
										Unreleased = firstEntry.Unreleased,
										Version = grouping.Key
									};
								})
								.OrderByDescending(cl => cl.Version)
								.ToList()))
			};

			Debug.Assert(releaseNotes.Components.ContainsKey(Component.Core) && releaseNotes.Components[Component.Core].Count == milestonesToProcess.Count);

			if (previousNotes != null)
			{
				foreach (var component in Enum.GetValues<Component>())
				{
					if (!previousNotes.Components.ContainsKey(component))
						continue;

					if (releaseNotes.Components.TryGetValue(component, out var changelists))
						releaseNotes.Components[component] = changelists
							.Concat(previousNotes.Components[component])
							.OrderByDescending(cl => cl.Version)
							.ToList();
					else
						releaseNotes.Components[component] = previousNotes.Components[component];
				}
			}

			foreach (var kvp in releaseNotes.Components)
			{
				var distinctCount = kvp.Value.Select(changelist => changelist.Version.ToString()).Distinct().Count();
				Debug.Assert(distinctCount == kvp.Value.Count);

				foreach (var cl in kvp.Value)
					cl.DeduplicateChanges();
			}

			return releaseNotes;
		}
	}
}
