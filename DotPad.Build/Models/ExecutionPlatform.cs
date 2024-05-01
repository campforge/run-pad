using NuGet.Versioning;
using System;

namespace DotPad.Build
{
	public class ExecutionPlatform
	{
		public string Name { get; }
		public string TargetFrameworkMoniker { get; }
		public string Description { get; }
		internal NuGetVersion Version { get; }

		internal ExecutionPlatform(
			string name,
			string targetFrameworkMoniker,
			NuGetVersion version
		)
		{
			Name = name;
			TargetFrameworkMoniker = targetFrameworkMoniker;
			Version = version; 
			Description = $"{name} {version}";
		}

		public override string ToString() => Description;
	}
}
