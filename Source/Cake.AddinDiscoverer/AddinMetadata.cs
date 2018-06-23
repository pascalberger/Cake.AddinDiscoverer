﻿using Cake.Incubator;
using System;
using System.Diagnostics;

namespace Cake.AddinDiscoverer
{
	[DebuggerDisplay("Name = {Name}")]
	internal class AddinMetadata
	{
		private Uri repositoryUrl;

		public string Name { get; set; }

		public string Maintainer { get; set; }

		public string GithubRepoName { get; private set; }

		public string GithubRepoOwner { get; private set; }

		public string[] Frameworks { get; set; }

		public DllReference[] References { get; set; }

		public AddinAnalysisResult AnalysisResult { get; set; }

		public Uri IconUrl { get; set; }

		public string NugetPackageVersion { get; set; }

		public Uri NugetPackageUrl { get; set; }

		public Uri GithubRepoUrl
		{
			get
			{
				return repositoryUrl;
			}

			set
			{
				repositoryUrl = value;

				if (value != null)
				{
					var parts = value.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2)
					{
						this.GithubRepoOwner = parts[0];
						this.GithubRepoName = parts[1];
					}
				}
			}
		}

		public Uri GithubIssueUrl { get; set; }

		public int? GithubIssueId { get; set; }

		public AddinType Type { get; set; }

		public bool IsDeprecated { get; set; }

		public string Description { get; set; }

		public string[] Tags { get; set; }

		public string GetMaintainerName()
		{
			var maintainer = GithubRepoOwner ?? Maintainer;
			if (maintainer.EqualsIgnoreCase("cake-contrib")) maintainer = Maintainer;
			return maintainer;
		}
	}
}
