﻿using Cake.AddinDiscoverer.Utilities;
using Cake.Incubator.StringExtensions;
using Octokit;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Cake.AddinDiscoverer.Steps
{
	internal class FindGithubIssueStep : IStep
	{
		public bool PreConditionIsMet(DiscoveryContext context) => context.Options.CreateGithubIssue;

		public string GetDescription(DiscoveryContext context) => "Check if a Github issue has been created for addins that do not meet best pratices";

		public async Task ExecuteAsync(DiscoveryContext context)
		{
			context.Addins = await context.Addins
				.ForEachAsync(
					async addin =>
					{
						if (addin.GithubIssueUrl == null && addin.GithubRepoUrl != null)
						{
							var request = new RepositoryIssueRequest()
							{
								Creator = context.Options.GithubUsername,
								State = ItemStateFilter.Open,
								SortProperty = IssueSort.Created,
								SortDirection = SortDirection.Descending
							};

							try
							{
								var issues = await context.GithubClient.Issue.GetAllForRepository(addin.GithubRepoOwner, addin.GithubRepoName, request).ConfigureAwait(false);
								var issue = issues.FirstOrDefault(i => i.Title.EqualsIgnoreCase(Constants.ISSUE_TITLE) || i.Body.StartsWith("We performed an automated audit of your Cake addin", StringComparison.OrdinalIgnoreCase));

								if (issue != null)
								{
									addin.GithubIssueUrl = new Uri(issue.Url);
									addin.GithubIssueId = issue.Number;
								}
							}
							catch (Exception e)
							{
								addin.AnalysisResult.Notes += $"FindGithubIssueAsync: {e.GetBaseException().Message}{Environment.NewLine}";
							}
						}

						return addin;
					}, Constants.MAX_GITHUB_CONCURENCY)
				.ConfigureAwait(false);
		}
	}
}