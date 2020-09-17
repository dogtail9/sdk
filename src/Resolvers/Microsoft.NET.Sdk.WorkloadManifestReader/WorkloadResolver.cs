// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Microsoft.NET.Sdk.WorkloadManifestReader
{
    /// <remarks>
    /// This very specifically exposes only the functionality needed right now by the MSBuild workload resolver
    /// and by the template engine. More general APIs will be added later.
    /// </remarks>
    public class WorkloadResolver : IWorkloadResolver
    {
        private readonly Dictionary<WorkloadDefinitionId, WorkloadDefinition> _workloads = new Dictionary<WorkloadDefinitionId, WorkloadDefinition>();
        private readonly Dictionary<WorkloadPackId, WorkloadPack> _packs = new Dictionary<WorkloadPackId, WorkloadPack>();
        private string[] _platformIds;
        private readonly string _dotNetRootPath;

        private Func<string, bool>? _fileExistOverride;
        private Func<string, bool>? _directoryExistOverride;

        public WorkloadResolver(IWorkloadManifestProvider manifestProvider, string dotNetRootPath)
        {
            this._dotNetRootPath = dotNetRootPath;

            // eventually we may want a series of fallbacks here, as rids have in general
            // but for now, keep it simple
            var platformId = GetHostPlatformId();
            if (platformId != null)
            {
                _platformIds = new[] { platformId, "*" };
            }
            else
            {
                _platformIds = new[] { "*" };
            }

            var manifests = new List<WorkloadManifest>();

            foreach (var manifestStream in manifestProvider.GetManifests())
            {
                using (manifestStream)
                {
                    var manifest = WorkloadManifestReader.ReadWorkloadManifest(manifestStream);
                    manifests.Add(manifest);
                }
            }

            foreach (var manifest in manifests)
            {
                foreach (var workload in manifest.Workloads)
                {
                    _workloads.Add(workload.Key, workload.Value);
                }
                foreach (var pack in manifest.Packs)
                {
                    _packs.Add(pack.Key, pack.Value);
                }
            }
        }


        // rather that forcing all consumers to depend on and parse the RID catalog, or doing that here, for now just bake in a small
        // subset of dev host platform rids for now for the workloads that are likely to need this functionality soonest
        private string? GetHostPlatformId()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "osx-x64",
                    Architecture.Arm64 => "osx-arm64",
                    _ => null
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    return "windows-x64";
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the installed workload packs of a particular kind
        /// </summary>
        /// <remarks>
        /// Used by MSBuild resolver to scan SDK packs for AutoImport.props files to be imported.
        /// Used by template engine to find templates to be added to hive.
        /// </remarks>
        public IEnumerable<PackInfo> GetInstalledWorkloadPacksOfKind(WorkloadPackKind kind)
        {
            foreach (var pack in _packs)
            {
                if (pack.Value.Kind != kind)
                {
                    continue;
                }

                var aliasedId = pack.Value.TryGetAliasForPlatformIds(_platformIds) ?? pack.Value.Id;
                var packPath = GetPackPath(_dotNetRootPath, aliasedId, pack.Value.Version, pack.Value.Kind);

                if (PackExists(packPath, kind))
                {
                    yield return new PackInfo(
                        pack.Value.Id.ToString(),
                        pack.Value.Version,
                        pack.Value.Kind,
                        packPath
                    );
                }
            }
        }

        internal void ReplaceFilesystemChecksForTest(Func<string, bool> fileExists, Func<string, bool> directoryExists)
        {
            _fileExistOverride = fileExists;
            _directoryExistOverride = directoryExists;
        }

        internal void ReplacePlatformIdsForTest(string[] platformIds)
        {
            this._platformIds = platformIds;
        }

        private bool PackExists (string packPath, WorkloadPackKind kind)
        {
            switch (kind)
            {
                case WorkloadPackKind.Framework:
                case WorkloadPackKind.Sdk:
                case WorkloadPackKind.Tool:
                    //can we do a more robust check than directory.exists?
                    return _directoryExistOverride?.Invoke(packPath) ?? Directory.Exists(packPath);
                case WorkloadPackKind.Library:
                case WorkloadPackKind.Template:
                    return _fileExistOverride?.Invoke(packPath) ?? File.Exists(packPath);
                default:
                    throw new ArgumentException($"The package kind '{kind}' is not known", nameof(kind));
            }
        }

        private static string GetPackPath (string dotNetRootPath, WorkloadPackId packageId, string packageVersion, WorkloadPackKind kind)
        {
            switch (kind)
            {
                case WorkloadPackKind.Framework:
                case WorkloadPackKind.Sdk:
                    return Path.Combine(dotNetRootPath, "packs", packageId.ToString(), packageVersion);
                case WorkloadPackKind.Template:
                    return Path.Combine(dotNetRootPath, "template-packs", packageId.GetNuGetCanonicalId() + "." + packageVersion.ToLowerInvariant() + ".nupkg");
                case WorkloadPackKind.Library:
                    return Path.Combine(dotNetRootPath, "library-packs", packageId.GetNuGetCanonicalId() + "." + packageVersion.ToLowerInvariant() + ".nupkg");
                case WorkloadPackKind.Tool:
                    return Path.Combine(dotNetRootPath, "tool-packs", packageId.ToString(), packageVersion);
                default:
                    throw new ArgumentException($"The package kind '{kind}' is not known", nameof(kind));
            }
        }

        public IEnumerable<string> GetPacksInWorkload(string workloadId)
        {
            if (string.IsNullOrWhiteSpace(workloadId))
            {
                throw new ArgumentException($"'{nameof(workloadId)}' cannot be null or whitespace", nameof(workloadId));
            }

            var id = new WorkloadDefinitionId(workloadId);

            if (!_workloads.TryGetValue(id, out var workload))
            {
                throw new Exception("Workload not found");
            }

            if (workload.Extends?.Count > 0)
            {
                return ExpandWorkload(workload).Select (p => p.ToString());
            }

            return workload.Packs.Select(p => p.ToString()) ?? Enumerable.Empty<string>();
        }

        private IEnumerable<WorkloadPackId> ExpandWorkload (WorkloadDefinition workload)
        {
            var dedup = new HashSet<WorkloadDefinitionId>();

            IEnumerable<WorkloadPackId> ExpandPacks (WorkloadDefinitionId workloadId)
            {
                if (!_workloads.TryGetValue (workloadId, out var workloadInfo))
                {
                    // inconsistent manifest
                    throw new Exception("Workload not found");
                }

                if (workloadInfo.Packs != null && workloadInfo.Packs.Count > 0)
                {
                    foreach (var p in workloadInfo.Packs)
                    {
                        yield return p;
                    }
                }

                if (workloadInfo.Extends != null && workloadInfo.Extends.Count > 0)
                {
                    foreach (var e in workloadInfo.Extends)
                    {
                        if (dedup.Add(e))
                        {
                            foreach (var ep in ExpandPacks(e))
                            {
                                yield return ep;
                            }
                        }
                    }
                }
            }

            return ExpandPacks(workload.Id);
        }

        /// <summary>
        /// Gets the version of a workload pack for this resolver's SDK band
        /// </summary>
        /// <remarks>
        /// Used by the MSBuild SDK resolver to look up which versions of the SDK packs to import.
        /// </remarks>
        public string? TryGetPackVersion(string packId)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException($"'{nameof(packId)}' cannot be null or whitespace", nameof(packId));
            }

            if (_packs.TryGetValue(new WorkloadPackId (packId), out var packInfo))
            {
                return packInfo.Version;
            }

            return null;
        }

        /// <summary>
        /// Recommends a set of workloads should be installed on top of the existing installed workloads to provide the specified missing packs
        /// </summary>
        /// <remarks>
        /// Used by the MSBuild workload resolver to emit actionable errors
        /// </remarks>
        public IList<WorkloadInfo> GetWorkloadSuggestionForMissingPacks(IList<string> packIds)
        {
            var installedPacks = new HashSet<WorkloadPackId>();
            var requestedPacks = new HashSet<WorkloadPackId>(packIds.Select(p => new WorkloadPackId(p)));

            foreach (var pack in _packs)
            {
                var aliasedId = pack.Value.TryGetAliasForPlatformIds(_platformIds) ?? pack.Value.Id;
                var packPath = GetPackPath(_dotNetRootPath, aliasedId, pack.Value.Version, pack.Value.Kind);

                if (PackExists(packPath, pack.Value.Kind))
                {
                    installedPacks.Add(pack.Key);
                }
            }

            // find workloads that contain any of the requested packs
            var incompleteCandidates = new List<WorkloadSuggestionCandidate>();
            var completeCandidates = new HashSet<WorkloadSuggestionCandidate>();

            foreach (var workload in _workloads)
            {
                var expanded = new HashSet<WorkloadPackId>(ExpandWorkload(workload.Value));
                if (expanded.Any(e => requestedPacks.Contains(e)))
                {
                    var stillMissing = new HashSet<WorkloadPackId>(requestedPacks.Where(p => !expanded.Contains(p)));
                    var satisfied = requestedPacks.Count - stillMissing.Count;

                    var candidate = new WorkloadSuggestionCandidate(new HashSet<WorkloadDefinitionId>() { workload.Key }, expanded, stillMissing);

                    if (candidate.IsComplete)
                    {
                        completeCandidates.Add(candidate);
                    }
                    else
                    {
                        incompleteCandidates.Add(candidate);
                    }
                }
            }

            //find all valid complete permutations by recursively exploring possible branches from a root
            void FindCandidates (WorkloadSuggestionCandidate root, List<WorkloadSuggestionCandidate> branches)
            {
                foreach (var branch in branches)
                {
                    //skip branches identical to ones that have already already been taken
                    //there's probably a more efficient way to do this but this is easy to reason about
                    if (root.Workloads.IsSupersetOf(branch.Workloads))
                    {
                        continue;
                    }

                    //skip branches that don't reduce the number of missing packs
                    //the branch may be a more optimal solution, but this will be handled elsewhere in the permuation space where it is treated as a root
                    if (!root.StillMissingPacks.Overlaps(branch.Packs))
                    {
                        continue;
                    }

                    //construct the new condidate by combining the root and the branch
                    var combinedIds = new HashSet<WorkloadDefinitionId>(root.Workloads);
                    combinedIds.UnionWith(branch.Workloads);
                    var combinedPacks = new HashSet<WorkloadPackId>(root.Packs);
                    combinedPacks.UnionWith(branch.Packs);
                    var stillMissing = new HashSet<WorkloadPackId>(root.StillMissingPacks);
                    stillMissing.ExceptWith(branch.StillMissingPacks);
                    var candidate = new WorkloadSuggestionCandidate(combinedIds, combinedPacks, stillMissing);

                    //if the candidate contains all the requested packs, it's complete. else, recurse to try adding more branches to it.
                    if (candidate.IsComplete)
                    {
                        completeCandidates.Add(candidate);
                    }
                    else
                    {
                        FindCandidates(candidate, branches);
                    }
                }
            }

            foreach (var c in incompleteCandidates)
            {
                FindCandidates(c, incompleteCandidates);
            }

            throw new NotImplementedException();
        }

        private class WorkloadSuggestionCandidate : IEquatable<WorkloadSuggestionCandidate>
        {
            public WorkloadSuggestionCandidate(HashSet<WorkloadDefinitionId> id, HashSet<WorkloadPackId> packs, HashSet<WorkloadPackId> missingPacks)
            {
                Packs = packs;
                StillMissingPacks = missingPacks;
                Workloads = id;
            }

            public HashSet<WorkloadDefinitionId> Workloads { get; }
            public HashSet<WorkloadPackId> Packs { get; }
            public HashSet<WorkloadPackId> StillMissingPacks { get; }
            public bool IsComplete => StillMissingPacks.Count == 0;

            public bool Equals(WorkloadSuggestionCandidate other) => Workloads.SetEquals(other.Workloads);

            public override int GetHashCode()
            {
                int hashcode = 0;
                foreach(var id in Workloads)
                {
                    hashcode ^= id.GetHashCode();
                }
                return hashcode;
            }
        }

        public class PackInfo
        {
            public PackInfo(string id, string version, WorkloadPackKind kind, string path)
            {
                Id = id;
                Version = version;
                Kind = kind;
                Path = path;
            }

            public string Id { get; }

            public string Version { get; }

            public WorkloadPackKind Kind { get; }

            /// <summary>
            /// Path to the pack. If it's a template or library pack, <see cref="IsStillPacked"/> will be <code>true</code> and this will be a path to the <code>nupkg</code>,
            /// else <see cref="IsStillPacked"/> will be <code>false</code> and this will be a path to the directory into which it has been unpacked.
            /// </summary>
            public string Path { get; }

            /// <summary>
            /// Whether the pack pointed to by the path is still in a packed form.
            /// </summary>
            public bool IsStillPacked => Kind switch
            {
                WorkloadPackKind.Library => false,
                WorkloadPackKind.Template => false,
                _ => true
            };
        }

        public class WorkloadInfo
        {
            public WorkloadInfo(string id, string description)
            {
                Id = id;
                Description = description;
            }

            public string Id { get; }
            public string Description { get; }
        }
    }
}
