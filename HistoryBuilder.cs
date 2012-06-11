﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GitImporter
{
    public class HistoryBuilder
    {
        public static TraceSource Logger = Program.Logger;

        private readonly RawHistoryBuilder _rawHistoryBuilder;

        /// <summary>
        /// _roots are directory whose parents have not been requested :
        /// they will therefore never appear in the Content of a DirectoryVersion
        /// </summary>
        private readonly HashSet<string> _roots = new HashSet<string>();

        /// <summary>
        /// For each branch, its parent branch
        /// </summary>
        private Dictionary<string, string> _globalBranches;
        private Dictionary<string, LabelInfo> _labels;

        private List<ChangeSet> _flattenChangeSets;
        private int _currentIndex = 0;
        private readonly Dictionary<string, ChangeSet> _startedBranches = new Dictionary<string, ChangeSet>();
        private readonly Dictionary<Tuple<string, string>, MergeInfo> _merges = new Dictionary<Tuple<string, string>, MergeInfo>();

        public HistoryBuilder(VobDB vobDB)
        {
            _rawHistoryBuilder = new RawHistoryBuilder(vobDB);
        }

        public void SetBranchFilters(string[] branches)
        {
            _rawHistoryBuilder.SetBranchFilters(branches);
        }

        public void SetRoots(IEnumerable<string> roots)
        {
            _roots.Clear();
            foreach (var root in roots)
                _roots.Add(root);
        }

        public IList<ChangeSet> Build()
        {
            _flattenChangeSets = _rawHistoryBuilder.Build();
            _globalBranches = _rawHistoryBuilder.GlobalBranches;
            _labels = _rawHistoryBuilder.Labels;
            return ProcessElementNames();
        }

        private List<ChangeSet> ProcessElementNames()
        {
            Logger.TraceData(TraceEventType.Start | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Start process element names");
            int n = 0;

            // same content than _flattenChangeSets, but not necessarily same order
            var orderedChangeSets = new List<ChangeSet>(_flattenChangeSets.Count);

            var branchTips = new Dictionary<string, ChangeSet>();
            // an element may appear under different names, especially during a move,
            // if the destination directory has been checked in before source directory
            var elementsNamesByBranch = new Dictionary<string, Dictionary<Element, HashSet<string>>>();
            // branch and version for which the elementName could not be found
            var orphanedVersionsByElement = new Dictionary<Element, List<Tuple<string, ChangeSet.NamedVersion>>>();
            // some moves (rename) may be in separate ChangeSets, we must be able to know what version to write at the new location
            var elementsVersionsByBranch = new Dictionary<string, Dictionary<Element, ElementVersion>>();
            // orphan versions that completed a label : should be orphans till the end
            var labeledOrphans = new HashSet<ElementVersion>();

            var applyNowChangeSets = new Queue<ChangeSet>();

            while (true)
            {
                ChangeSet changeSet;
                if (applyNowChangeSets.Count > 0)
                    changeSet = applyNowChangeSets.Dequeue();
                else
                {
                    applyNowChangeSets = new Queue<ChangeSet>(FindNextChangeSets());
                    if (applyNowChangeSets.Count > 0)
                        changeSet = applyNowChangeSets.Dequeue();
                    else
                        // done !
                        break;
                }

                n++;
                // n is 1-based index because it is used as a mark id for git fast-import, that reserves id 0
                changeSet.Id = n;
                orderedChangeSets.Add(changeSet);
                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Start process element names in ChangeSet", changeSet);
                if (n % 1000 == 0)
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet, "Processing element names in ChangeSet", changeSet);

                branchTips[changeSet.Branch] = changeSet;
                Dictionary<Element, HashSet<string>> elementsNames;
                Dictionary<Element, ElementVersion> elementsVersions;
                bool isNewBranch = !_startedBranches.ContainsKey(changeSet.Branch);
                if (isNewBranch)
                {
                    if (changeSet.Branch != "main")
                    {
                        string parentBranch = _globalBranches[changeSet.Branch];
                        changeSet.BranchingPoint = branchTips[parentBranch];
                        branchTips[parentBranch].IsBranchingPoint = true;
                        elementsNames = new Dictionary<Element, HashSet<string>>(elementsNamesByBranch[parentBranch]);
                        elementsVersions = new Dictionary<Element, ElementVersion>(elementsVersionsByBranch[parentBranch]);
                    }
                    else
                    {
                        elementsNames = new Dictionary<Element, HashSet<string>>();
                        elementsVersions = new Dictionary<Element, ElementVersion>();
                    }
                    elementsNamesByBranch.Add(changeSet.Branch, elementsNames);
                    elementsVersionsByBranch.Add(changeSet.Branch, elementsVersions);
                    _startedBranches.Add(changeSet.Branch, changeSet.BranchingPoint);
                }
                else
                {
                    elementsNames = elementsNamesByBranch[changeSet.Branch];
                    elementsVersions = elementsVersionsByBranch[changeSet.Branch];
                }

                var changeSetBuilder = new ChangeSetBuilder(changeSet, elementsNames, elementsVersions, orphanedVersionsByElement, _roots);
                var orphanedVersions = changeSetBuilder.Build();

                ProcessLabels(changeSet, elementsVersions, orphanedVersions, labeledOrphans);

                Logger.TraceData(TraceEventType.Start | TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Stop process element names in ChangeSet", changeSet.Id);
            }

            // really lost versions
            var lostVersions = new HashSet<ElementVersion>();
            foreach (var orphanedVersions in orphanedVersionsByElement.Values)
                foreach (var orphanedVersion in orphanedVersions)
                {
                    var lostVersion = orphanedVersion.Item2.Version;
                    lostVersions.Add(lostVersion);
                    labeledOrphans.Remove(lostVersion);
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Version " + lostVersion + " has not been visible in any imported directory version");
                }

            // now that we now what versions to ignore :
            foreach (var changeSet in orderedChangeSets)
                ProcessMerges(changeSet, lostVersions);

            ComputeAllMerges();

            // labeled orphans that unexpectedly found a parent
            foreach (var orphan in labeledOrphans)
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                 "Version " + orphan + " has been labeled while an orphan");

            // uncompleted labels
            foreach (var label in _labels.Values)
            {
                foreach (var missingVersion in label.MissingVersions)
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet,
                                     "Version " + missingVersion + " with label " + label.Name + " not seen");
                Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label.Name + " has " + label.MissingVersions.Count + " missing versions : not applied");
            }

            // we may need to reorder a little bit in case of merges where the "To" is before the "From"
            var result = new List<ChangeSet>(orderedChangeSets.Count);
            for (int i = 0; i < orderedChangeSets.Count; i++)
                AddChangeSet(orderedChangeSets, result, i);

            Logger.TraceData(TraceEventType.Stop | TraceEventType.Information, (int)TraceId.CreateChangeSet, "Stop process element names");

            return result;
        }

        private IEnumerable<ChangeSet> FindNextChangeSets()
        {
            ChangeSet changeSet = null;
            while (changeSet == null)
                if (_currentIndex < _flattenChangeSets.Count)
                    changeSet = _flattenChangeSets[_currentIndex++];
                else
                    return new ChangeSet[0];
            
            // stay on current : maybe we won't return changeSet, so we will need to process it next time
            _currentIndex--;

            var wouldBreakLabels = FindWouldBreakLabels(changeSet);
            if (wouldBreakLabels.Count == 0)
            {
                _flattenChangeSets[_currentIndex++] = null;
                return new[] { changeSet };
            }
            // we look for a label that could be completed with changeSets within the next four hours
            foreach (var pair in wouldBreakLabels)
            {
                int index = _currentIndex;
                string label = pair.Item1;
                string branch = pair.Item2;
                var labelInfo = _labels[label];
                HashSet<ElementVersion> missingVersions;
                if (!labelInfo.MissingVersions.TryGetValue(branch, out missingVersions))
                {
                    // this means we would break a spawning branch : we simply look for the first ChangeSet on this branch
                    ChangeSet spawningPoint = FindBranchToSpawn(labelInfo, branch);
                    Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                        string.Format("Applying {0} before {1} to start branch {2}", spawningPoint, changeSet, spawningPoint.Branch));
                    return new[] { spawningPoint };
                }
                // we are only interested in missing versions on branch associated with the label as broken
                // versions on parent branch are OK (else we would already have failed),
                // versions on children branches will be handled later (when the branch spawns)
                missingVersions = new HashSet<ElementVersion>(missingVersions);
                var lastVersion = missingVersions.OrderBy(v => v.Date.Ticks).Last();
                if ((lastVersion.Date - changeSet.FinishTime).TotalHours > 4.0)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                        string.Format("Label {0} broken at {1}, missing version {2} ({3}) is too late : not applied", label, changeSet, lastVersion, lastVersion.Date));
                    _labels.Remove(label);
                    continue;
                }
                var neededChangeSets = new HashSet<int>();
                while (missingVersions.Count > 0 && index < _flattenChangeSets.Count)
                {
                    var toCheck = _flattenChangeSets[index++];
                    if (toCheck == null || toCheck.Branch != branch)
                        continue;
                    foreach (var version in toCheck.Versions)
                        if (missingVersions.Contains(version.Version))
                        {
                            neededChangeSets.Add(index - 1);
                            missingVersions.Remove(version.Version);
                        }
                }
                if (missingVersions.Count > 0)
                    throw new Exception("Label " + label + " could not be completed : versions " +
                        string.Join(", ", missingVersions.Select(v => v.ToString())) + " not in any further ChangeSet on branch " + changeSet.Branch);

                bool isLabelConsistent = ProcessDependenciesAndCheckLabelConsistency(neededChangeSets, label, index);
                if (!isLabelConsistent)
                {
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label + " broken at " + changeSet + " : not applied");
                    _labels.Remove(label);
                    continue;
                }
                // everything is OK : applying all neededChangeSets will complete label for this branch
                var result = new List<ChangeSet>();
                foreach (int i in neededChangeSets.OrderBy(i => i))
                {
                    result.Add(_flattenChangeSets[i]);
                    _flattenChangeSets[i] = null;
                }
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                    string.Format("Applying {0} ChangeSets before {1} to complete label {2}", result.Count, changeSet, label));
                return result;
            }
            // too bad, no WouldBeBroken label can be completed, simply go on
            _flattenChangeSets[_currentIndex++] = null;
            return new[] { changeSet };
        }

        private ChangeSet FindBranchToSpawn(LabelInfo labelInfo, string branch)
        {
            string missingBranch = null;
            foreach (var key in labelInfo.MissingVersions.Keys)
            {
                missingBranch = key;
                while (missingBranch != null && !_startedBranches.ContainsKey(missingBranch) &&
                       _globalBranches[missingBranch] != branch)
                    missingBranch = _globalBranches[missingBranch];
                if (missingBranch != null && !_startedBranches.ContainsKey(missingBranch) && _globalBranches[missingBranch] == branch)
                    // found it !
                    break;
                missingBranch = null;
            }
            if (missingBranch == null)
                throw new Exception("Could not find missing branch of " + labelInfo.Name + " leading to " + branch);
            for (int i = _currentIndex; i < _flattenChangeSets.Count; i++)
                if (_flattenChangeSets[i] != null && _flattenChangeSets[i].Branch == missingBranch)
                {
                    var result = _flattenChangeSets[i];
                    _flattenChangeSets[i] = null;
                    return result;
                }
            throw new Exception("Could not find spawning ChangeSet of " + missingBranch);
        }

        private bool ProcessDependenciesAndCheckLabelConsistency(HashSet<int> neededChangeSets, string label, int index)
        {
            var allNewVersions = neededChangeSets.Select(i => _flattenChangeSets[i])
                .SelectMany(c => c.Versions).Select(v => v.Version)
                .GroupBy(v => v.Element).ToDictionary(g => g.Key, g => new List<ElementVersion>(g));
            // process backwards to get dependencies of dependencies
            // this may include ChangeSets on a parent branch
            bool isLabelConsistent = true;
            for (int i = index - 1; i >= _currentIndex; i--)
            {
                var toCheck = _flattenChangeSets[i];
                if (toCheck == null)
                    continue;
                if (neededChangeSets.Contains(i))
                {
                    if (FindWouldBreakLabels(toCheck).Any(l => l.Item1 == label))
                    {
                        isLabelConsistent = false;
                        break;
                    }
                    continue;
                }
                if (!toCheck.Versions.Select(v => v.Version)
                         .Any(v =>
                         {
                             List<ElementVersion> l;
                             return allNewVersions.TryGetValue(v.Element, out l) && l.Any(v.IsAncestorOf);
                         }))
                    continue;
                if (FindWouldBreakLabels(toCheck).Any(l => l.Item1 == label))
                {
                    isLabelConsistent = false;
                    break;
                }
                neededChangeSets.Add(i);
                foreach (var version in toCheck.Versions.Select(v => v.Version))
                    allNewVersions.AddToCollection(version.Element, version);
            }
            return isLabelConsistent;
        }

        private HashSet<Tuple<string, string>> FindWouldBreakLabels(ChangeSet changeSet)
        {
            var result = new HashSet<Tuple<string, string>>();
            if (!_startedBranches.ContainsKey(changeSet.Branch))
            {
                // then we would break any label on this new branch that is not complete on all parent branches
                string parentBranch = _globalBranches[changeSet.Branch];
                while (parentBranch != null)
                {
                    foreach (var labelInfo in _labels.Values
                        .Where(l => l.MissingVersions.ContainsKey(changeSet.Branch) && l.MissingVersions.ContainsKey(parentBranch)))
                        result.Add(new Tuple<string, string>(labelInfo.Name, parentBranch));
                    parentBranch = _globalBranches[parentBranch];
                }
            }
            foreach (var label in changeSet.Versions
                .Select(v =>
                    {
                        var prev = v.Version.GetPreviousVersion();
                        while (prev != null && prev.VersionNumber == 0)
                            prev = prev.GetPreviousVersion();
                        return prev;
                    })
                .Where(v => v != null)
                .SelectMany(v => v.Labels
                                .Where(label =>
                                           {
                                               LabelInfo labelInfo;
                                               if (!_labels.TryGetValue(label, out labelInfo))
                                                   return false;
                                               // if changeSet.Branch is not "finished" for the label -> broken
                                               if (labelInfo.MissingVersions.ContainsKey(changeSet.Branch))
                                                   return true;
                                               // ok if all branches starting from changeSet.Branch on which the label exists are started
                                               foreach (var branch in labelInfo.MissingVersions.Keys)
                                               {
                                                   var missingBranch = branch;
                                                   while (missingBranch != null && !_startedBranches.ContainsKey(missingBranch))
                                                   {
                                                       var parent = _globalBranches[missingBranch];
                                                       if (parent == changeSet.Branch)
                                                           // we need to spawn missingBranch before applying changeSet
                                                           return true;
                                                       missingBranch = parent;
                                                   }
                                               }
                                               return false;
                                           })))
                result.Add(new Tuple<string, string>(label, changeSet.Branch));

            return result;
        }

        private void ProcessLabels(ChangeSet changeSet, Dictionary<Element, ElementVersion> elementsVersions, List<ElementVersion> orphanedVersions, HashSet<ElementVersion> labeledOrphans)
        {
            var finishedLabels = new HashSet<string>();
            foreach (var version in orphanedVersions)
            {
                // we should usually not complete a label here : the version of the parent directory with the label should not have been seen
                // this can still happen if the version only appears in directories not imported (meaning they will be reported in "really lost versions")
                changeSet.Labels.AddRange(ProcessVersionLabels(version, elementsVersions, finishedLabels));
                if (finishedLabels.Count > 0)
                {
                    labeledOrphans.Add(version);
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Label(s) " + string.Join(", ", finishedLabels) + " has been completed with orphan version " + version);
                }
            }
            foreach (var namedVersion in changeSet.Versions)
                changeSet.Labels.AddRange(ProcessVersionLabels(namedVersion.Version, elementsVersions, finishedLabels));
        }

        private IEnumerable<string> ProcessVersionLabels(ElementVersion version, Dictionary<Element, ElementVersion> elementsVersions, HashSet<string> finishedLabels)
        {
            var result = new List<string>();
            foreach (var label in version.Labels)
            {
                LabelInfo labelInfo;
                if (!_labels.TryGetValue(label, out labelInfo))
                    continue;
                labelInfo.MissingVersions.RemoveFromCollection(version.Branch.BranchName, version);
                if (labelInfo.MissingVersions.Count > 0)
                    continue;
                Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " completed with version " + version);
                // so we removed the last missing version, check that everything is still OK
                _labels.Remove(label);
                finishedLabels.Add(label);
                bool ok = true;
                foreach (var toCheck in labelInfo.Versions)
                {
                    ElementVersion inCurrentVersions;
                    elementsVersions.TryGetValue(toCheck.Element, out inCurrentVersions);
                    if ((inCurrentVersions == null && toCheck.VersionNumber != 0) ||
                        (inCurrentVersions != null && inCurrentVersions != toCheck))
                    {
                        string msg = "Label " + label + " is inconsistent : should be on " + toCheck +
                            (inCurrentVersions == null ? ", but element has no current version" : ", not on " + inCurrentVersions);
                        Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, msg);
                        ok = false;
                    }
                }
                if (!ok)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet, "Label " + label + " was inconsistent : not applied");
                else
                {
                    Logger.TraceData(TraceEventType.Verbose, (int)TraceId.CreateChangeSet, "Label " + label + " was applied");
                    result.Add(label);
                }
            }
            return result;
        }

        private void ProcessMerges(ChangeSet changeSet, HashSet<ElementVersion> lostVersions)
        {
            foreach (var version in changeSet.Versions.Select(v => v.Version))
            {
                foreach (var mergeTo in version.MergesTo)
                    ProcessMerge(changeSet, version, mergeTo, false, lostVersions);

                foreach (var mergeFrom in version.MergesFrom)
                    ProcessMerge(changeSet, mergeFrom, version, true, lostVersions);
            }

            // merge to skipped versions are OK
            foreach (var version in changeSet.SkippedVersions)
                foreach (var mergeFrom in version.MergesFrom)
                    ProcessMerge(changeSet, mergeFrom, version, true, lostVersions);
        }

        private void ProcessMerge(ChangeSet changeSet, ElementVersion fromVersion, ElementVersion toVersion, bool isToVersionInChangeSet, HashSet<ElementVersion> lostVersions)
        {
            if (lostVersions.Contains(fromVersion) || lostVersions.Contains(toVersion))
                return;

            var from = fromVersion.Branch.BranchName;
            var to = toVersion.Branch.BranchName;
            // for now we only merge back to parent branch, assuming other merges are cherry-picking
            string fromParent;
            if (!_globalBranches.TryGetValue(from, out fromParent) || fromParent != to)
                return;
            // since version 0 is identical to branching point : not interesting
            if (fromVersion.VersionNumber == 0)
                return;
            // handle several merges with a common end : we consider only the latest
            if (!IsLatestMerge(fromVersion, toVersion))
                return;

            var key = new Tuple<string, string>(from, to);
            MergeInfo mergeInfo;
            if (!_merges.TryGetValue(key, out mergeInfo))
            {
                mergeInfo = new MergeInfo(from, to);
                _merges.Add(key, mergeInfo);
            }
            // a version may be seen several times if it appears under a new name,
            // or if a move of files happened in several steps, needing to re-create them (instead of simply moving them)
            if (isToVersionInChangeSet)
            {
                if (mergeInfo.SeenToVersions.Contains(toVersion))
                    return;
                mergeInfo.SeenToVersions.Add(toVersion);
                // either the fromVersion has already been seen, and toVersion is in MissingToVersions
                // or we add fromVersion to MissingFromVersions
                ChangeSet fromChangeSet;
                if (mergeInfo.MissingToVersions.TryGetValue(toVersion, out fromChangeSet))
                {
                    mergeInfo.MissingToVersions.Remove(toVersion);
                    var missingInChangeSet = mergeInfo.MissingToVersionsByChangeSet[fromChangeSet];
                    missingInChangeSet.Remove(toVersion);
                    if (missingInChangeSet.Count == 0)
                    {
                        mergeInfo.MissingToVersionsByChangeSet.Remove(fromChangeSet);
                        mergeInfo.Merges[fromChangeSet] = changeSet;
                    }
                }
                else
                    mergeInfo.MissingFromVersions[fromVersion] = changeSet;
            }
            else
            {
                if (mergeInfo.SeenFromVersions.Contains(fromVersion))
                    return;
                mergeInfo.SeenFromVersions.Add(fromVersion);
                // either toVersion has already been seen, and fromVersion is in MissingFromVersions
                // or we add toVersion to MissingToVersions
                ChangeSet toChangeSet;
                if (mergeInfo.MissingFromVersions.TryGetValue(fromVersion, out toChangeSet))
                {
                    mergeInfo.MissingFromVersions.Remove(fromVersion);
                    ChangeSet existingTo;
                    if (!mergeInfo.Merges.TryGetValue(changeSet, out existingTo) || existingTo.Id < toChangeSet.Id)
                        mergeInfo.Merges[changeSet] = toChangeSet;
                }
                else
                {
                    mergeInfo.MissingToVersions[toVersion] = changeSet;
                    mergeInfo.MissingToVersionsByChangeSet.AddToCollection(changeSet, toVersion);
                }
            }
        }

        private static bool IsLatestMerge(ElementVersion fromVersion, ElementVersion toVersion)
        {
            return !fromVersion.MergesTo.Any(v => v.Branch == toVersion.Branch && v.VersionNumber > toVersion.VersionNumber) &&
                !toVersion.MergesFrom.Any(v => v.Branch == fromVersion.Branch && v.VersionNumber > fromVersion.VersionNumber);
        }

        private static void AddChangeSet(List<ChangeSet> source, List<ChangeSet> destination, int sourceIndex)
        {
            var changeSet = source[sourceIndex];
            if (changeSet == null)
                return;

            // add missing changeSets on other branches needed to have all merges available
            foreach (var fromChangeSet in changeSet.Merges.Where(c => c.Id > changeSet.Id && source[c.Id - 1] != null))
            {
                Logger.TraceData(TraceEventType.Information, (int)TraceId.CreateChangeSet,
                    "Reordering : ChangeSet " + fromChangeSet + "  must be imported before " + changeSet);
                if (fromChangeSet != source[fromChangeSet.Id - 1])
                    throw new Exception("Inconsistent Id for " + fromChangeSet +
                        " : changeSet at corresponding index was " + (source[fromChangeSet.Id - 1] == null ? "null" : source[fromChangeSet.Id - 1].ToString()));
                for (int i = sourceIndex + 1; i < fromChangeSet.Id; i++)
                    if (source[i] != null && source[i].Branch == fromChangeSet.Branch)
                        AddChangeSet(source, destination, i);
            }
            
            destination.Add(changeSet);
        }

        private void ComputeAllMerges()
        {
            foreach (var mergeInfo in _merges.Values)
            {
                int currentTo = int.MaxValue;
                var fromChangeSets = mergeInfo.Merges.Keys.OrderByDescending(c => c.Id);
                foreach (var from in fromChangeSets)
                {
                    ChangeSet to = null;
                    foreach (var candidate in fromChangeSets.Where(c => c.Id <= from.Id).Select(c => mergeInfo.Merges[c]))
                        if (to == null || candidate.Id > to.Id)
                            to = candidate;

                    // we cannot merge before branching point !!
                    if (to != null && to.Id < currentTo)
                    {
                        if (to.Id > _startedBranches[from.Branch].Id)
                        {
                            Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                "Invalid merge from " + from + " to " + to + " : branch " + mergeInfo.From + " branched from later changeSet " + _startedBranches[mergeInfo.From]);
                            // no hope to have another meaningful merge
                            break;
                        }
                        currentTo = to.Id;
                        to.Merges.Add(from);
                        from.IsMerged = true;
                    }
                }
                // incomplete merges
                if (mergeInfo.MissingFromVersions.Count > 0 || mergeInfo.MissingToVersions.Count > 0)
                    Logger.TraceData(TraceEventType.Warning, (int)TraceId.CreateChangeSet,
                                     "Merge " + mergeInfo + " could not be completed : " +
                                     (mergeInfo.MissingFromVersions.Count > 0 ? "missing fromVersions : " + string.Join(", ", mergeInfo.MissingFromVersions.Select(v => v.Key.ToString())) : "") +
                                     (mergeInfo.MissingToVersions.Count > 0 ? "missing toVersions : " + string.Join(", ", mergeInfo.MissingToVersions.Select(v => v.Key.ToString())) : ""));
            }
        }
    }
}
