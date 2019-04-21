﻿using Cake.AddinDiscoverer.Utilities;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cake.AddinDiscoverer.Steps
{
	internal class SynchronizeYamlStep : IStep
	{
		public bool PreConditionIsMet(DiscoveryContext context) => context.Options.SynchronizeYaml;

		public string GetDescription(DiscoveryContext context) => "Synchronize yml files on the Cake web site";

		public async Task ExecuteAsync(DiscoveryContext context)
		{
			// Arbitrary max number of files to delete, add and modify in a given commit.
			// This is to avoid AbuseException when commiting too many files.
			const int MAX_FILES_TO_COMMIT = 75;

			// Ensure the fork is up-to-date
			var fork = await context.GithubClient.RefreshFork(context.Options.GithubUsername, Constants.CAKE_WEBSITE_REPO_NAME).ConfigureAwait(false);
			var upstream = fork.Parent;

			// --------------------------------------------------
			// Discover if any files need to be added/deleted/modified
			var directoryContent = await context.GithubClient.Repository.Content.GetAllContents(Constants.CAKE_REPO_OWNER, Constants.CAKE_WEBSITE_REPO_NAME, "addins").ConfigureAwait(false);
			var yamlFiles = directoryContent
				.Where(file => file.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
				.Where(file => !string.IsNullOrEmpty(context.Options.AddinName) ? Path.GetFileNameWithoutExtension(file.Name) == context.Options.AddinName : true)
				.ToArray();

			var yamlToBeDeleted = yamlFiles
				.Where(f =>
				{
					var addin = context.Addins.FirstOrDefault(a => a.Name == Path.GetFileNameWithoutExtension(f.Name));
					return addin == null || addin.IsDeprecated;
				})
				.Where(f => f.Name != "Magic-Chunks.yml") // Ensure that MagicChunk's yaml file is not deleted despite the fact that is doesn't follow the naming convention. See: https://github.com/cake-build/website/issues/535#issuecomment-399692891
				.Take(MAX_FILES_TO_COMMIT)
				.OrderBy(f => f.Name)
				.ToArray();

			var addinsWithContent = await context.Addins
				.Where(addin => !addin.IsDeprecated)
				.Where(addin => addin.Type == AddinType.Addin)
				.Where(addin => yamlFiles.Any(f => Path.GetFileNameWithoutExtension(f.Name) == addin.Name))
				.Take(MAX_FILES_TO_COMMIT)
				.ForEachAsync(
					async addin =>
					{
						var contents = await context.GithubClient.Repository.Content.GetAllContents(Constants.CAKE_REPO_OWNER, Constants.CAKE_WEBSITE_REPO_NAME, $"addins/{addin.Name}.yml").ConfigureAwait(false);
						return new
						{
							Addin = addin,
							CurrentContent = contents[0].Content,
							NewContent = GenerateYamlFile(context, addin)
						};
					}, Constants.MAX_NUGET_CONCURENCY)
				.ConfigureAwait(false);

			var addinsToBeUpdated = addinsWithContent
				.Where(addin => addin.CurrentContent != addin.NewContent)
				.OrderBy(addin => addin.Addin.Name)
				.ToArray();

			var addinsToBeCreated = context.Addins
				.Where(addin => !addin.IsDeprecated)
				.Where(addin => addin.Type == AddinType.Addin)
				.Where(addin => !yamlFiles.Any(f => Path.GetFileNameWithoutExtension(f.Name) == addin.Name))
				.Take(MAX_FILES_TO_COMMIT)
				.OrderBy(addin => addin.Name)
				.Select(addin => new
				{
					Addin = addin,
					CurrentContent = string.Empty,
					NewContent = GenerateYamlFile(context, addin)
				})
				.ToArray();

			if (yamlToBeDeleted.Any())
			{
				var issueTitle = "Delete YAML files";

				// Check if an issue already exists
				var issue = await Misc.FindGithubIssueAsync(context, upstream.Owner.Login, upstream.Name, context.Options.GithubUsername, issueTitle).ConfigureAwait(false);
				if (issue == null)
				{
					// Create issue
					var newIssue = new NewIssue(issueTitle)
					{
						Body = $"The Cake.AddinDiscoverer tool has discovered discrepencies between the YAML files currently on Cake's web site and the packages discovered on NuGet.org:{Environment.NewLine}" +
							$"{Environment.NewLine}The following YAML files found on Cake's web site do not have a corresponding NuGet package. Therefore they must be deleted:{Environment.NewLine}" +
							string.Join(Environment.NewLine, yamlToBeDeleted.Select(f => $"- {f.Name}")) + Environment.NewLine
					};
					issue = await context.GithubClient.Issue.Create(Constants.CAKE_REPO_OWNER, Constants.CAKE_WEBSITE_REPO_NAME, newIssue).ConfigureAwait(false);

					// Commit changes to a new branch and submit PR
					var newBranchName = $"delete_yaml_files_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}";
					var commits = new List<(string CommitMessage, IEnumerable<string> FilesToDelete, IEnumerable<(EncodingType Encoding, string Path, string Content)> FilesToUpsert)>
				{
					(CommitMessage: "Delete YAML files that do not have a corresponding NuGet package", FilesToDelete: yamlToBeDeleted.Select(y => y.Path).ToArray(), FilesToUpsert: null)
				};

					await Misc.CommitToNewBranchAndSubmitPullRequestAsync(context, fork, issue, newBranchName, issueTitle, commits).ConfigureAwait(false);
				}
			}

			if (addinsToBeCreated.Any())
			{
				var issueTitle = "Add YAML files";

				// Check if an issue already exists
				var issue = await Misc.FindGithubIssueAsync(context, upstream.Owner.Login, upstream.Name, context.Options.GithubUsername, issueTitle).ConfigureAwait(false);
				if (issue == null)
				{
					// Create issue
					var newIssue = new NewIssue(issueTitle)
					{
						Body = $"The Cake.AddinDiscoverer tool has discovered discrepencies between the YAML files currently on Cake's web site and the packages discovered on NuGet.org:{Environment.NewLine}" +
							$"{Environment.NewLine}The following packages found on NuGet's web site do not have a corresponding YAML file. Therefore a YAML file must be created for each:{Environment.NewLine}" +
							string.Join(Environment.NewLine, addinsToBeCreated.Select(a => $"- {a.Addin.Name}")) + Environment.NewLine
					};
					issue = await context.GithubClient.Issue.Create(Constants.CAKE_REPO_OWNER, Constants.CAKE_WEBSITE_REPO_NAME, newIssue).ConfigureAwait(false);

					// Commit changes to a new branch and submit PR
					var newBranchName = $"add_yaml_files_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}";
					var commits = new List<(string CommitMessage, IEnumerable<string> FilesToDelete, IEnumerable<(EncodingType Encoding, string Path, string Content)> FilesToUpsert)>
				{
					(CommitMessage: "Add YAML files for NuGet packages we discovered", FilesToDelete: null, FilesToUpsert: addinsToBeCreated.Select(addin => (Encoding: EncodingType.Utf8, Path: $"addins/{addin.Addin.Name}.yml", Content: addin.NewContent)).ToArray())
				};

					await Misc.CommitToNewBranchAndSubmitPullRequestAsync(context, fork, issue, newBranchName, issueTitle, commits).ConfigureAwait(false);
				}
			}

			if (addinsToBeUpdated.Any())
			{
				var issueTitle = "Update YAML files";

				// Check if an issue already exists
				var issue = await Misc.FindGithubIssueAsync(context, upstream.Owner.Login, upstream.Name, context.Options.GithubUsername, issueTitle).ConfigureAwait(false);
				if (issue == null)
				{
					// Create issue
					var newIssue = new NewIssue(issueTitle)
					{
						Body = $"The Cake.AddinDiscoverer tool has discovered discrepencies between the YAML files currently on Cake's web site and the packages discovered on NuGet.org:{Environment.NewLine}" +
							$"{Environment.NewLine}The content of the following YAML files does not match the metadata in their corresponding NuGet package. Therefore the YAML files need to be updated:{Environment.NewLine}" +
						string.Join(Environment.NewLine, addinsToBeUpdated.Select(a => $"- {a.Addin.Name}")) + Environment.NewLine
					};
					issue = await context.GithubClient.Issue.Create(Constants.CAKE_REPO_OWNER, Constants.CAKE_WEBSITE_REPO_NAME, newIssue).ConfigureAwait(false);

					// Commit changes to a new branch and submit PR
					var newBranchName = $"update_yaml_files_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}";
					var commits = new List<(string CommitMessage, IEnumerable<string> FilesToDelete, IEnumerable<(EncodingType Encoding, string Path, string Content)> FilesToUpsert)>
				{
					(CommitMessage: "Update YAML files to match metadata from NuGet", FilesToDelete: null, FilesToUpsert: addinsToBeUpdated.Select(addin => (Encoding: EncodingType.Utf8, Path: $"addins/{addin.Addin.Name}.yml", Content: addin.NewContent)).ToArray())
				};

					await Misc.CommitToNewBranchAndSubmitPullRequestAsync(context, fork, issue, newBranchName, issueTitle, commits).ConfigureAwait(false);
				}
			}
		}

		private static string GenerateYamlFile(DiscoveryContext context, AddinMetadata addin)
		{
			var yamlContent = new StringBuilder();

			yamlContent.AppendUnixLine($"Name: {addin.Name}");
			yamlContent.AppendUnixLine($"NuGet: {addin.Name}");
			yamlContent.AppendUnixLine("Assemblies:");
			yamlContent.AppendUnixLine($"- \"/**/{addin.DllName}\"");
			yamlContent.AppendUnixLine($"Repository: {addin.GithubRepoUrl ?? addin.NuGetPackageUrl}");
			yamlContent.AppendUnixLine($"Author: {addin.GetMaintainerName()}");
			yamlContent.AppendUnixLine($"Description: \"{addin.Description}\"");
			if (addin.IsPrerelease) yamlContent.AppendUnixLine("Prerelease: \"true\"");
			yamlContent.AppendUnixLine("Categories:");
			yamlContent.AppendUnixLine(GetCategoriesForYaml(context, addin.Tags));

			return yamlContent.ToString();
		}

		private static string GetCategoriesForYaml(DiscoveryContext context, IEnumerable<string> tags)
		{
			var filteredAndFormatedTags = tags
				.Except(context.BlacklistedTags, StringComparer.InvariantCultureIgnoreCase)
				.Select(tag => tag.TrimStart("Cake-", StringComparison.InvariantCultureIgnoreCase))
				.Distinct()
				.Select(tag => $"- {tag}");

			var categories = string.Join(Environment.NewLine, filteredAndFormatedTags);

			return categories;
		}
	}
}