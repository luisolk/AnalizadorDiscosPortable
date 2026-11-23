using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AnalizadorDiscosPortable
{
    public static class DuplicateScanner
    {
        private static readonly HashSet<string> IgnoredGenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "binaries", "data", "assets", "content", "resources", "common", "plugins",
            "locales", "locale", "lang", "languages", "shaders", "textures", "audio", "sound",
            "sounds", "music", "video", "videos", "movies", "scripts", "config", "configs",
            "configuration", "settings", "profiles", "profile", "logs", "log", "temp", "tmp",
            "cache", "caches", "gpucache", "code cache", "shadercache", "d3dscache", "crashdumps",
            "backup", "backups", "updates", "update", "patch", "patches", "dlc", "mods",
            "workshop", "saves", "saved", "save", "screenshots", "captures", "driver", "drivers",
            "runtime", "redist", " _commonredist", "directx", "vcredist", "dotnet", "support",
            "tools", "utils", "docs", "documentation", "help", "licenses", "license", "legal",
            "unins000", "unins001", "uninstall", "uninstaller", "setup", "install", "installer",
            "launcher", "updater", "crashreporter", "crashhandler", "unityplayer",
            "unitycrashhandler64", "unitycrashhandler32", "unrealcefsubprocess", "cefsharp",
            "libcef", "chrome_elf", "d3dcompiler_47", "d3dcompiler_43", "vcruntime140",
            "vcruntime140_1", "msvcp140", "ucrtbase", "openal32", "steam_api", "steam_api64",
            "eosovh", "easyanticheat", "battleye", "globalgamemanagers", "level0", "level1",
            "level2", "sharedassets0", "sharedassets1", "sharedassets2", "main", "default",
            "index", "app", "client", "server", "game", "engine", "core", "base", "system",
            "windows", "microsoft", "google", "mozilla", "adobe", "nvidia", "intel", "amd",
            "package", "packages", "release", "debug", "x64", "x86", "win64", "win32",
            "build", "dist", "out", "output", "src", "source", "lib", "libs", "include",
            "media", "images", "img", "icons", "fonts", "styles", "themes", "templates",
            "user", "users", "public", "program files", "program files (x86)", "programdata",
            "appdata", "local", "roaming", "locallow", "programs", "desktop", "documents",
            "downloads", "pictures", "steamlibrary", "steamapps", "xboxgames", "epic games",
            "riot games", "games", "juegos", "programas", "apps", "portable"
        };

        private static readonly Regex RxSceneAndEditionTags = new Regex(
            @"\b(steamrip\.com|steamrip|fitgirl|repack|elamigos|dodi|gog|portable|full|installer|setup|install|x64|x86|win64|win32|64-bit|32-bit|64bit|32bit|amd64|arm64|en-us|es-es|es-mx|multi\d*)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxCopySuffix = new Regex(
            @"(\(\s*\d+\s*\)|\[\s*\d+\s*\]|-\s*copia(\s*\(\d+\))?|-\s*copy(\s*\(\d+\))?|_copy\d*|_copia\d*|_old\d*|_bak\d*|_backup\d*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxVersionNumbers = new Regex(
            @"\b(v?\d+\.\d+(\.\d+){0,3}([a-z0-9\-_]*)?|v\d+|20[12]\d|r\d{2,4}|build[\s_\-]*\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<DuplicateGroup> AnalyzeAllDuplicatesAndSimilars(
            List<DiskItem> disks,
            List<InstallItem> installations)
        {
            List<DuplicateGroup> allGroups = new List<DuplicateGroup>();
            HashSet<string> claimedInstallFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (installations != null)
            {
                for (int i = 0; i < installations.Count; i++)
                {
                    string loc = FormatUtils.NormalizePath(installations[i].InstallLocation);
                    if (!string.IsNullOrEmpty(loc) && loc.Length > 3)
                    {
                        claimedInstallFolders.Add(loc);
                    }
                }
            }

            // Collect candidate files and folders from scanned disks (or quick-scan user/root directories if a disk isn't scanned yet)
            List<FsNode> candidateFiles = new List<FsNode>(8192);
            List<FsNode> candidateFolders = new List<FsNode>(2048);

            CollectCandidateNodes(disks, claimedInstallFolders, candidateFiles, candidateFolders);

            // 1. Detect Repeated or Multi-Version Installations (and cross-check with installer/archive files!)
            DetectRepeatedInstallations(installations, candidateFiles, allGroups);

            // 2. Detect Similar / Repeated Folders across disks or directories
            DetectSimilarFolders(candidateFolders, claimedInstallFolders, allGroups);

            // 3. Detect Exact Duplicate Files & Different-Version Files
            DetectDuplicateAndVersionedFiles(candidateFiles, claimedInstallFolders, allGroups);

            // Sort groups by PotentialSavingsBytes descending (then TotalSizeBytes descending)
            allGroups.Sort(delegate(DuplicateGroup a, DuplicateGroup b)
            {
                // Put Repeated Installations with >0 bytes or multiple copies first when savings are similar
                int cmp = b.PotentialSavingsBytes.CompareTo(a.PotentialSavingsBytes);
                if (cmp != 0) return cmp;
                return b.TotalSizeBytes.CompareTo(a.TotalSizeBytes);
            });

            return allGroups;
        }

        private static void DetectRepeatedInstallations(
            List<InstallItem> installations,
            List<FsNode> candidateFiles,
            List<DuplicateGroup> outGroups)
        {
            if (installations == null || installations.Count == 0) return;

            Dictionary<string, List<DuplicateEntry>> byBaseKey = new Dictionary<string, List<DuplicateEntry>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> displayTitleByBaseKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < installations.Count; i++)
            {
                InstallItem inst = installations[i];
                if (string.IsNullOrWhiteSpace(inst.Name)) continue;

                string extractedVer;
                string canonical = ExtractCanonicalName(inst.Name, false, out extractedVer);
                if (string.IsNullOrEmpty(canonical) || canonical.Length < 3)
                    continue;

                string compactKey = ToAlphaNumericKey(canonical);
                if (compactKey.Length < 3 || IgnoredGenericNames.Contains(canonical) || IgnoredGenericNames.Contains(compactKey))
                    continue;

                List<DuplicateEntry> list;
                if (!byBaseKey.TryGetValue(compactKey, out list))
                {
                    list = new List<DuplicateEntry>();
                    byBaseKey[compactKey] = list;
                    displayTitleByBaseKey[compactKey] = CleanDisplayTitle(canonical, inst.Name);
                }

                // Avoid adding the exact same physical folder twice in the same group
                string normLoc = FormatUtils.NormalizePath(inst.InstallLocation);
                bool sameFolderExists = false;
                if (!string.IsNullOrEmpty(normLoc))
                {
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (string.Equals(FormatUtils.NormalizePath(list[j].FullPath), normLoc, StringComparison.OrdinalIgnoreCase))
                        {
                            sameFolderExists = true;
                            break;
                        }
                    }
                }
                if (sameFolderExists) continue;

                DuplicateEntry entry = new DuplicateEntry();
                entry.Name = inst.Name;
                entry.VersionOrVariant = !string.IsNullOrEmpty(inst.Version)
                    ? ("v" + inst.Version)
                    : (!string.IsNullOrEmpty(extractedVer) ? extractedVer : inst.SourceType);
                entry.FullPath = !string.IsNullOrEmpty(inst.InstallLocation) ? inst.InstallLocation : ("(Registro Windows - " + inst.DriveLetter + ")");
                entry.DriveLetter = inst.DriveLetter;
                entry.SizeBytes = inst.SizeBytes;
                entry.SourceDescription = inst.SourceType;
                entry.CreatedDate = inst.CreatedDate;
                entry.LastModified = inst.LastModified;
                entry.Install = inst;
                entry.Node = inst.LinkedNode;

                list.Add(entry);
            }

            // Also check if any heavy archive (.zip, .rar, .7z, .iso) or installer (.exe, .msi) >= 20 MB matches an installed program's base key!
            if (candidateFiles != null)
            {
                for (int i = 0; i < candidateFiles.Count; i++)
                {
                    FsNode file = candidateFiles[i];
                    if (file.Size < 20L * 1024L * 1024L) continue;
                    string ext = (file.Extension ?? "").ToLowerInvariant();
                    if (ext != ".zip" && ext != ".rar" && ext != ".7z" && ext != ".iso" && ext != ".exe" && ext != ".msi")
                        continue;

                    string ver;
                    string can = ExtractCanonicalName(file.Name, true, out ver);
                    string compact = ToAlphaNumericKey(can);
                    if (compact.Length < 4) continue;

                    List<DuplicateEntry> existingInstList;
                    if (byBaseKey.TryGetValue(compact, out existingInstList) && existingInstList.Count > 0)
                    {
                        // Make sure this file is NOT inside the installation's own folder!
                        bool isInsideInstall = false;
                        string normFile = FormatUtils.NormalizePath(file.FullPath);
                        for (int j = 0; j < existingInstList.Count; j++)
                        {
                            string instPath = FormatUtils.NormalizePath(existingInstList[j].FullPath);
                            if (!string.IsNullOrEmpty(instPath) && !instPath.StartsWith("(") &&
                                normFile.StartsWith(instPath + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                isInsideInstall = true;
                                break;
                            }
                        }
                        if (isInsideInstall) continue;

                        DuplicateEntry fileEntry = new DuplicateEntry();
                        fileEntry.Name = file.Name;
                        fileEntry.VersionOrVariant = !string.IsNullOrEmpty(ver) ? ("Instalador/Archivo " + ver) : ("Archivo " + ext.ToUpperInvariant());
                        fileEntry.FullPath = file.FullPath;
                        fileEntry.DriveLetter = file.DriveLetter;
                        fileEntry.SizeBytes = file.Size;
                        fileEntry.SourceDescription = "Archivo Instalador / Comprimido en Disco";
                        fileEntry.CreatedDate = file.CreatedDate;
                        fileEntry.LastModified = file.LastModified;
                        fileEntry.Node = file;
                        existingInstList.Add(fileEntry);
                    }
                }
            }

            foreach (KeyValuePair<string, List<DuplicateEntry>> kv in byBaseKey)
            {
                List<DuplicateEntry> entries = kv.Value;
                if (entries.Count < 2) continue;

                entries.Sort(delegate(DuplicateEntry a, DuplicateEntry b)
                {
                    return b.SizeBytes.CompareTo(a.SizeBytes);
                });

                long total = 0;
                long maxSingle = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    total += entries[i].SizeBytes;
                    if (entries[i].SizeBytes > maxSingle) maxSingle = entries[i].SizeBytes;
                }

                DuplicateGroup grp = new DuplicateGroup();
                grp.CanonicalBaseName = kv.Key;
                grp.DisplayGroupTitle = displayTitleByBaseKey[kv.Key];
                grp.Kind = DuplicateMatchKind.RepeatedInstallation;
                grp.TotalSizeBytes = total;
                grp.PotentialSavingsBytes = Math.Max(0L, total - maxSingle);

                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].ParentGroup = grp;
                    grp.Entries.Add(entries[i]);
                }

                outGroups.Add(grp);
            }
        }

        private static void DetectSimilarFolders(
            List<FsNode> candidateFolders,
            HashSet<string> claimedInstallFolders,
            List<DuplicateGroup> outGroups)
        {
            if (candidateFolders == null || candidateFolders.Count == 0) return;

            // Track keys already emitted by installations so we don't duplicate the same group
            HashSet<string> existingGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < outGroups.Count; i++)
            {
                existingGroupKeys.Add(outGroups[i].CanonicalBaseName);
            }

            Dictionary<string, List<DuplicateEntry>> byFolderKey = new Dictionary<string, List<DuplicateEntry>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> titleByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < candidateFolders.Count; i++)
            {
                FsNode folder = candidateFolders[i];
                if (folder.Size < 25L * 1024L * 1024L) continue; // Only folders >= 25 MB

                string normPath = FormatUtils.NormalizePath(folder.FullPath);
                if (claimedInstallFolders.Contains(normPath)) continue;

                string ver;
                string canonical = ExtractCanonicalName(folder.Name, false, out ver);
                string compact = ToAlphaNumericKey(canonical);
                if (compact.Length < 4 || IgnoredGenericNames.Contains(canonical) || IgnoredGenericNames.Contains(compact) ||
                    IgnoredGenericNames.Contains(folder.Name))
                    continue;

                if (existingGroupKeys.Contains(compact)) continue;

                List<DuplicateEntry> list;
                if (!byFolderKey.TryGetValue(compact, out list))
                {
                    list = new List<DuplicateEntry>();
                    byFolderKey[compact] = list;
                    titleByKey[compact] = CleanDisplayTitle(canonical, folder.Name);
                }

                // Ensure no parent/child folder pair is grouped with itself
                bool isNested = false;
                for (int j = 0; j < list.Count; j++)
                {
                    string otherPath = FormatUtils.NormalizePath(list[j].FullPath);
                    if (normPath.StartsWith(otherPath + "\\", StringComparison.OrdinalIgnoreCase) ||
                        otherPath.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        isNested = true;
                        break;
                    }
                }
                if (isNested) continue;

                DuplicateEntry entry = new DuplicateEntry();
                entry.Name = folder.Name;
                entry.VersionOrVariant = !string.IsNullOrEmpty(ver) ? ver : ("Carpeta en " + folder.DriveLetter);
                entry.FullPath = folder.FullPath;
                entry.DriveLetter = folder.DriveLetter;
                entry.SizeBytes = folder.Size;
                entry.SourceDescription = string.Format("Carpeta ({0} archivos)", FormatUtils.FormatNumber(folder.FileCount));
                entry.CreatedDate = folder.CreatedDate;
                entry.LastModified = folder.LastModified;
                entry.Node = folder;
                list.Add(entry);
            }

            foreach (KeyValuePair<string, List<DuplicateEntry>> kv in byFolderKey)
            {
                List<DuplicateEntry> entries = kv.Value;
                if (entries.Count < 2) continue;

                entries.Sort(delegate(DuplicateEntry a, DuplicateEntry b)
                {
                    return b.SizeBytes.CompareTo(a.SizeBytes);
                });

                long total = 0;
                long maxSingle = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    total += entries[i].SizeBytes;
                    if (entries[i].SizeBytes > maxSingle) maxSingle = entries[i].SizeBytes;
                }

                DuplicateGroup grp = new DuplicateGroup();
                grp.CanonicalBaseName = kv.Key;
                grp.DisplayGroupTitle = titleByKey[kv.Key];
                grp.Kind = DuplicateMatchKind.SimilarFolder;
                grp.TotalSizeBytes = total;
                grp.PotentialSavingsBytes = Math.Max(0L, total - maxSingle);

                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].ParentGroup = grp;
                    grp.Entries.Add(entries[i]);
                }

                outGroups.Add(grp);
            }
        }

        private static void DetectDuplicateAndVersionedFiles(
            List<FsNode> candidateFiles,
            HashSet<string> claimedInstallFolders,
            List<DuplicateGroup> outGroups)
        {
            if (candidateFiles == null || candidateFiles.Count == 0) return;

            // Pass 1: Exact Duplicates (Same canonical name or exact filename + exact same SizeBytes >= 2 MB)
            Dictionary<string, List<FsNode>> exactBuckets = new Dictionary<string, List<FsNode>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < candidateFiles.Count; i++)
            {
                FsNode f = candidateFiles[i];
                if (f.Size < 2L * 1024L * 1024L) continue; // >= 2 MB
                if (IsIgnoredEngineOrSystemFile(f.Name, f.FullPath)) continue;

                string ext = (f.Extension ?? "").ToLowerInvariant();
                string baseWithoutCopy = StripCopySuffixOnly(Path.GetFileNameWithoutExtension(f.Name));
                if (string.IsNullOrEmpty(baseWithoutCopy) || baseWithoutCopy.Length < 3) continue;
                if (IgnoredGenericNames.Contains(baseWithoutCopy)) continue;

                string exactKey = baseWithoutCopy.ToLowerInvariant() + ext + "|" + f.Size.ToString();
                List<FsNode> list;
                if (!exactBuckets.TryGetValue(exactKey, out list))
                {
                    list = new List<FsNode>();
                    exactBuckets[exactKey] = list;
                }
                list.Add(f);
            }

            HashSet<FsNode> alreadyInExactGroup = new HashSet<FsNode>();

            foreach (KeyValuePair<string, List<FsNode>> kv in exactBuckets)
            {
                List<FsNode> files = kv.Value;
                if (files.Count < 2) continue;

                // If all copies are deep inside the exact same installation folder, skip internal game asset mirrors
                if (AreAllInsideSameInstallation(files, claimedInstallFolders))
                    continue;

                long singleSize = files[0].Size;
                long totalSize = singleSize * files.Count;

                DuplicateGroup grp = new DuplicateGroup();
                grp.CanonicalBaseName = kv.Key;
                grp.DisplayGroupTitle = files[0].Name;
                grp.Kind = DuplicateMatchKind.IdenticalFile;
                grp.TotalSizeBytes = totalSize;
                grp.PotentialSavingsBytes = totalSize - singleSize;

                for (int i = 0; i < files.Count; i++)
                {
                    FsNode f = files[i];
                    alreadyInExactGroup.Add(f);

                    DuplicateEntry entry = new DuplicateEntry();
                    entry.Name = f.Name;
                    entry.VersionOrVariant = "Copia idéntica (" + FormatUtils.FormatBytes(f.Size) + ")";
                    entry.FullPath = f.FullPath;
                    entry.DriveLetter = f.DriveLetter;
                    entry.SizeBytes = f.Size;
                    entry.SourceDescription = "Archivo Idéntico (" + (f.Extension ?? "") + ")";
                    entry.CreatedDate = f.CreatedDate;
                    entry.LastModified = f.LastModified;
                    entry.Node = f;
                    entry.ParentGroup = grp;
                    grp.Entries.Add(entry);
                }

                outGroups.Add(grp);
            }

            // Pass 2: Different Versions or Variants of the Same Base Filename (>= 5 MB, outside installed game internals)
            Dictionary<string, List<DuplicateEntry>> versionBuckets = new Dictionary<string, List<DuplicateEntry>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> versionTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < candidateFiles.Count; i++)
            {
                FsNode f = candidateFiles[i];
                if (f.Size < 5L * 1024L * 1024L) continue; // >= 5 MB
                if (alreadyInExactGroup.Contains(f)) continue;
                if (IsIgnoredEngineOrSystemFile(f.Name, f.FullPath)) continue;

                // Only match versioned/similar files when they are user/standalone files (not internal game engine pak/assets inside an installation)
                if (IsInsideAnyInstallation(f.FullPath, claimedInstallFolders))
                    continue;

                string ver;
                string canonical = ExtractCanonicalName(f.Name, true, out ver);
                string compact = ToAlphaNumericKey(canonical);
                if (compact.Length < 4 || IgnoredGenericNames.Contains(canonical) || IgnoredGenericNames.Contains(compact))
                    continue;

                string extGroup = GetExtensionFamily(f.Extension);
                string bucketKey = compact + "|" + extGroup;

                List<DuplicateEntry> list;
                if (!versionBuckets.TryGetValue(bucketKey, out list))
                {
                    list = new List<DuplicateEntry>();
                    versionBuckets[bucketKey] = list;
                    versionTitles[bucketKey] = CleanDisplayTitle(canonical, Path.GetFileNameWithoutExtension(f.Name)) + " (" + extGroup + ")";
                }

                DuplicateEntry entry = new DuplicateEntry();
                entry.Name = f.Name;
                entry.VersionOrVariant = !string.IsNullOrEmpty(ver) ? ver : (f.Extension ?? "");
                entry.FullPath = f.FullPath;
                entry.DriveLetter = f.DriveLetter;
                entry.SizeBytes = f.Size;
                entry.SourceDescription = "Versión / Variante (" + (f.Extension ?? "") + ")";
                entry.CreatedDate = f.CreatedDate;
                entry.LastModified = f.LastModified;
                entry.Node = f;
                list.Add(entry);
            }

            foreach (KeyValuePair<string, List<DuplicateEntry>> kv in versionBuckets)
            {
                List<DuplicateEntry> entries = kv.Value;
                if (entries.Count < 2) continue;

                entries.Sort(delegate(DuplicateEntry a, DuplicateEntry b)
                {
                    return b.SizeBytes.CompareTo(a.SizeBytes);
                });

                long total = 0;
                long maxSingle = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    total += entries[i].SizeBytes;
                    if (entries[i].SizeBytes > maxSingle) maxSingle = entries[i].SizeBytes;
                }

                DuplicateGroup grp = new DuplicateGroup();
                grp.CanonicalBaseName = kv.Key;
                grp.DisplayGroupTitle = versionTitles[kv.Key];
                grp.Kind = DuplicateMatchKind.VersionedOrSimilarFile;
                grp.TotalSizeBytes = total;
                grp.PotentialSavingsBytes = Math.Max(0L, total - maxSingle);

                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].ParentGroup = grp;
                    grp.Entries.Add(entries[i]);
                }

                outGroups.Add(grp);
            }
        }

        private static void CollectCandidateNodes(
            List<DiskItem> disks,
            HashSet<string> claimedInstallFolders,
            List<FsNode> outFiles,
            List<FsNode> outFolders)
        {
            if (disks == null) return;

            for (int d = 0; d < disks.Count; d++)
            {
                DiskItem disk = disks[d];
                if (disk.IsScanned && disk.RootNode != null)
                {
                    // Traverse scanned tree
                    Stack<KeyValuePair<FsNode, int>> stack = new Stack<KeyValuePair<FsNode, int>>(1024);
                    stack.Push(new KeyValuePair<FsNode, int>(disk.RootNode, 0));

                    while (stack.Count > 0)
                    {
                        KeyValuePair<FsNode, int> item = stack.Pop();
                        FsNode node = item.Key;
                        int depth = item.Value;

                        if (depth > 0)
                        {
                            string lowerPath = (node.FullPath ?? "").ToLowerInvariant();
                            if (lowerPath.StartsWith("c:\\windows\\") ||
                                lowerPath.Contains("\\$recycle.bin") ||
                                lowerPath.Contains("\\system volume information") ||
                                lowerPath.Contains("\\appdata\\local\\temp") ||
                                lowerPath.Contains("\\node_modules\\") ||
                                lowerPath.Contains("\\.git\\"))
                            {
                                continue;
                            }

                            if (node.IsFolder)
                            {
                                if (depth <= 3 && node.Size >= 25L * 1024L * 1024L)
                                {
                                    outFolders.Add(node);
                                }
                            }
                            else
                            {
                                if (node.Size >= 2L * 1024L * 1024L)
                                {
                                    outFiles.Add(node);
                                }
                            }
                        }

                        if (node.IsFolder && node.Children != null && depth < 7)
                        {
                            for (int i = 0; i < node.Children.Count; i++)
                            {
                                stack.Push(new KeyValuePair<FsNode, int>(node.Children[i], depth + 1));
                            }
                        }
                    }
                }
                else
                {
                    // Disk not full-scanned yet: do a super fast shallow check of top-level folders & common user dirs on this disk
                    QuickCollectUnscannedDiskCandidates(disk.DriveLetter, outFiles);
                }
            }
        }

        private static void QuickCollectUnscannedDiskCandidates(string driveLetter, List<FsNode> outFiles)
        {
            List<string> dirsToPeek = new List<string>();
            string root = driveLetter + "\\";
            dirsToPeek.Add(root);

            if (driveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase))
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    dirsToPeek.Add(Path.Combine(userProfile, "Downloads"));
                    dirsToPeek.Add(Path.Combine(userProfile, "Desktop"));
                    dirsToPeek.Add(Path.Combine(userProfile, "Documents"));
                    dirsToPeek.Add(Path.Combine(userProfile, "Videos"));
                }
            }
            else
            {
                try
                {
                    string[] topSubDirs = Directory.GetDirectories(root);
                    for (int i = 0; i < topSubDirs.Length && i < 30; i++)
                    {
                        string name = Path.GetFileName(topSubDirs[i]).ToLowerInvariant();
                        if (!name.StartsWith("$") && name != "system volume information")
                            dirsToPeek.Add(topSubDirs[i]);
                    }
                }
                catch { }
            }

            for (int i = 0; i < dirsToPeek.Count; i++)
            {
                string dir = dirsToPeek[i];
                if (!Directory.Exists(dir)) continue;
                try
                {
                    string[] files = Directory.GetFiles(dir);
                    for (int f = 0; f < files.Length; f++)
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(files[f]);
                            if (fi.Length >= 5L * 1024L * 1024L)
                            {
                                FsNode fn = new FsNode();
                                fn.Name = fi.Name;
                                fn.FullPath = fi.FullName;
                                fn.DriveLetter = driveLetter;
                                fn.IsFolder = false;
                                fn.Size = fi.Length;
                                fn.Extension = fi.Extension;
                                fn.CreatedDate = fi.CreationTime;
                                fn.LastModified = fi.LastWriteTime;
                                fn.Category = FormatUtils.ClassifyFile(fi.Name, fi.Extension, fi.FullName);
                                outFiles.Add(fn);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        public static string ExtractCanonicalName(string rawName, bool isFile, out string extractedVersionOrSuffix)
        {
            extractedVersionOrSuffix = "";
            if (string.IsNullOrWhiteSpace(rawName)) return "";

            string working = rawName.Trim();
            if (isFile)
            {
                try
                {
                    string withoutExt = Path.GetFileNameWithoutExtension(working);
                    if (!string.IsNullOrEmpty(withoutExt))
                        working = withoutExt;
                }
                catch { }
            }

            List<string> tags = new List<string>();

            // 1. Check copy suffixes like "(1)", "- copia"
            Match mCopy = RxCopySuffix.Match(working);
            if (mCopy.Success)
            {
                tags.Add(mCopy.Value.Trim());
                working = working.Substring(0, mCopy.Index).Trim();
            }

            // 2. Normalize _ and - first, then match scene/edition tags (including .com), then replace . with space
            working = working.Replace('_', ' ').Replace('-', ' ');

            // 3. Extract scene/edition/arch tags
            MatchCollection mcTags = RxSceneAndEditionTags.Matches(working);
            if (mcTags.Count > 0)
            {
                foreach (Match m in mcTags)
                {
                    tags.Add(m.Value);
                }
                working = RxSceneAndEditionTags.Replace(working, " ");
            }

            working = working.Replace('.', ' ');

            // 4. Extract version numbers (keep them in extractedVersionOrSuffix)
            MatchCollection mcVer = RxVersionNumbers.Matches(working);
            if (mcVer.Count > 0)
            {
                // Be careful not to strip numbers if the ENTIRE name is just a title + single number like "Cyberpunk 2077" or "Battlefield 2042"
                // Only strip version tokens if they have dots (e.g. 3.11), start with 'v'/'r'/'build', or if stripping leaves at least 3 letters
                foreach (Match m in mcVer)
                {
                    string val = m.Value;
                    bool looksLikeExplicitVersion = val.Contains(".") ||
                                                    val.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
                                                    val.StartsWith("r", StringComparison.OrdinalIgnoreCase) ||
                                                    val.StartsWith("build", StringComparison.OrdinalIgnoreCase);
                    if (looksLikeExplicitVersion)
                    {
                        tags.Add(val);
                        working = working.Replace(val, " ");
                    }
                }
            }

            // Remove leftover parens/brackets and collapse spaces
            working = Regex.Replace(working, @"[\(\)\[\]\{\}:™®©]", " ");
            working = Regex.Replace(working, @"\s+", " ").Trim();

            if (tags.Count > 0)
            {
                extractedVersionOrSuffix = string.Join(" ", tags.ToArray());
            }

            return working;
        }

        private static string StripCopySuffixOnly(string nameWithoutExt)
        {
            if (string.IsNullOrEmpty(nameWithoutExt)) return "";
            string s = nameWithoutExt.Trim();
            Match m = RxCopySuffix.Match(s);
            if (m.Success && m.Index >= 3)
            {
                return s.Substring(0, m.Index).Trim();
            }
            return s;
        }

        private static string ToAlphaNumericKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            char[] arr = s.ToLowerInvariant().ToCharArray();
            char[] res = new char[arr.Length];
            int idx = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (char.IsLetterOrDigit(arr[i]))
                {
                    res[idx++] = arr[i];
                }
            }
            return new string(res, 0, idx);
        }

        private static string CleanDisplayTitle(string canonical, string fallback)
        {
            if (string.IsNullOrWhiteSpace(canonical)) return fallback;
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(canonical.ToLowerInvariant());
        }

        private static string GetExtensionFamily(string ext)
        {
            string e = (ext ?? "").ToLowerInvariant();
            switch (e)
            {
                case ".exe":
                case ".msi":
                    return "Instalador/Ejecutable";
                case ".zip":
                case ".rar":
                case ".7z":
                case ".tar":
                case ".gz":
                case ".iso":
                case ".img":
                    return "Comprimido/ISO";
                case ".mp4":
                case ".mkv":
                case ".avi":
                case ".mov":
                case ".webm":
                    return "Video";
                case ".vhd":
                case ".vhdx":
                case ".vmdk":
                    return "Disco Virtual";
                default:
                    return string.IsNullOrEmpty(e) ? "Archivo" : e.ToUpperInvariant();
            }
        }

        private static bool IsIgnoredEngineOrSystemFile(string fileName, string fullPath)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            string lowerName = fileName.ToLowerInvariant();
            string noExt = Path.GetFileNameWithoutExtension(lowerName);

            if (IgnoredGenericNames.Contains(lowerName) || IgnoredGenericNames.Contains(noExt))
                return true;

            if (noExt.StartsWith("pakchunk") ||
                noExt.StartsWith("sharedassets") ||
                noExt.StartsWith("resources.assets") ||
                noExt.StartsWith("globalgamemanagers") ||
                noExt.StartsWith("level") ||
                noExt.StartsWith("unins00") ||
                lowerName == "pagefile.sys" ||
                lowerName == "hiberfil.sys" ||
                lowerName == "swapfile.sys")
            {
                return true;
            }

            if (!string.IsNullOrEmpty(fullPath))
            {
                string lp = fullPath.ToLowerInvariant();
                if (lp.StartsWith("c:\\windows\\") || lp.Contains("\\system volume information"))
                    return true;
            }

            return false;
        }

        private static bool IsInsideAnyInstallation(string fullPath, HashSet<string> claimedInstallFolders)
        {
            if (string.IsNullOrEmpty(fullPath) || claimedInstallFolders == null) return false;
            string norm = FormatUtils.NormalizePath(fullPath);
            foreach (string instFolder in claimedInstallFolders)
            {
                if (norm.StartsWith(instFolder + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool AreAllInsideSameInstallation(List<FsNode> files, HashSet<string> claimedInstallFolders)
        {
            if (files == null || files.Count < 2 || claimedInstallFolders == null) return false;
            string commonInst = null;
            for (int i = 0; i < files.Count; i++)
            {
                string norm = FormatUtils.NormalizePath(files[i].FullPath);
                string matchedInst = null;
                foreach (string instFolder in claimedInstallFolders)
                {
                    if (norm.StartsWith(instFolder + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedInst = instFolder;
                        break;
                    }
                }
                if (matchedInst == null) return false;
                if (commonInst == null) commonInst = matchedInst;
                else if (!string.Equals(commonInst, matchedInst, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return commonInst != null;
        }
    }
}
