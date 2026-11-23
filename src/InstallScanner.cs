using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AnalizadorDiscosPortable
{
    public static class InstallScanner
    {
        private static readonly HashSet<string> GenericContainerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static InstallScanner()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrEmpty(userProfile)) GenericContainerPaths.Add(FormatUtils.NormalizePath(userProfile));
            if (!string.IsNullOrEmpty(localAppData))
            {
                GenericContainerPaths.Add(FormatUtils.NormalizePath(localAppData));
                GenericContainerPaths.Add(FormatUtils.NormalizePath(Path.Combine(localAppData, "Programs")));
                GenericContainerPaths.Add(FormatUtils.NormalizePath(Path.Combine(localAppData, "Temp")));
                GenericContainerPaths.Add(FormatUtils.NormalizePath(Path.Combine(localAppData, "Microsoft")));
                GenericContainerPaths.Add(FormatUtils.NormalizePath(Path.Combine(localAppData, "Packages")));
            }
            if (!string.IsNullOrEmpty(roamingAppData))
            {
                GenericContainerPaths.Add(FormatUtils.NormalizePath(roamingAppData));
                GenericContainerPaths.Add(FormatUtils.NormalizePath(Path.Combine(roamingAppData, "Microsoft")));
            }
        }

        private static bool IsGenericContainerPath(string rawPath)
        {
            string p = FormatUtils.NormalizePath(rawPath);
            if (string.IsNullOrEmpty(p) || p.Length <= 3) return true;

            if (GenericContainerPaths.Contains(p)) return true;

            string withoutDrive = p.Length > 2 && p[1] == ':' ? p.Substring(2) : p;
            string lower = withoutDrive.ToLowerInvariant();

            switch (lower)
            {
                case "\\windows":
                case "\\windows\\system32":
                case "\\windows\\syswow64":
                case "\\windows\\installer":
                case "\\windows\\winsxs":
                case "\\program files":
                case "\\program files (x86)":
                case "\\program files\\common files":
                case "\\program files (x86)\\common files":
                case "\\programdata":
                case "\\programdata\\package cache":
                case "\\programdata\\microsoft":
                case "\\users":
                case "\\games":
                case "\\juegos":
                case "\\programas":
                case "\\apps":
                case "\\steamlibrary":
                case "\\steamlibrary\\steamapps":
                case "\\steamlibrary\\steamapps\\common":
                case "\\xboxgames":
                case "\\epic games":
                case "\\riot games":
                    return true;
            }

            if (lower.StartsWith("\\windows\\installer\\") || lower.StartsWith("\\programdata\\package cache\\"))
                return true;

            return false;
        }

        public static List<InstallItem> DetectAllInstallations(
            List<DiskItem> disks,
            Action<string> onStatus,
            Action<List<InstallItem>> onPartialListReady = null)
        {
            List<InstallItem> masterList = new List<InstallItem>();
            Dictionary<string, InstallItem> byPath = new Dictionary<string, InstallItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, InstallItem> byName = new Dictionary<string, InstallItem>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pre-scan Steam appmanifest_*.acf files on all disks for instant game sizes & paths
            if (onStatus != null) onStatus("Detectando librerías de Steam y juegos en todos los discos...");
            ScanSteamManifests(disks, masterList, byPath, byName, claimedPaths);

            // 2. Scan Windows Registry Uninstall keys (64-bit & 32-bit, HKLM & HKCU)
            if (onStatus != null) onStatus("Leyendo registro de instalaciones de Windows (64-bit y 32-bit)...");
            ScanRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", masterList, byPath, byName, claimedPaths);
            ScanRegistryHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", masterList, byPath, byName, claimedPaths);
            ScanRegistryHive(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", masterList, byPath, byName, claimedPaths);
            ScanRegistryHive(RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", masterList, byPath, byName, claimedPaths);

            // 3. Scan Microsoft Store / AppX Packages
            if (onStatus != null) onStatus("Detectando aplicaciones de Microsoft Store / AppX...");
            ScanAppXPackages(masterList, byPath, byName, claimedPaths);

            // 4. Scan Game Libraries & Program Folders across all detected disks (C:, D:, L:, etc.)
            if (onStatus != null) onStatus("Explorando carpetas de programas, juegos y portables en todos los discos...");
            ScanDisksForProgramsAndGames(disks, masterList, byPath, byName, claimedPaths);

            // Send initial list immediately so the user sees all installations within ~1 second!
            if (onPartialListReady != null)
            {
                List<InstallItem> initialSnapshot = new List<InstallItem>(masterList);
                initialSnapshot.Sort(delegate(InstallItem a, InstallItem b)
                {
                    int cmp = b.SizeBytes.CompareTo(a.SizeBytes);
                    if (cmp != 0) return cmp;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                onPartialListReady(initialSnapshot);
            }

            if (onStatus != null) onStatus(string.Format("Calculando peso en disco de {0} instalaciones detectadas...", masterList.Count));

            int processedCount = 0;
            int totalToProcess = masterList.Count;

            // 5. Calculate real folder sizes in parallel for installations that don't have size yet
            Parallel.ForEach(masterList, new ParallelOptions { MaxDegreeOfParallelism = 8 }, delegate(InstallItem item)
            {
                if (!item.SizeCalculatedFromDisk &&
                    !string.IsNullOrEmpty(item.InstallLocation) &&
                    !IsGenericContainerPath(item.InstallLocation) &&
                    Directory.Exists(item.InstallLocation))
                {
                    // Fast bounded size calculation (up to 900 directories per app; full disk scan refines to 100% later)
                    long diskBytes = NativeScanner.CalculateDirectorySizeFast(item.InstallLocation, 8, 900);
                    if (diskBytes > 0)
                    {
                        item.SizeBytes = Math.Max(diskBytes, item.RegistryEstimatedBytes);
                        item.SizeCalculatedFromDisk = true;
                    }
                    else if (item.RegistryEstimatedBytes > 0)
                    {
                        item.SizeBytes = item.RegistryEstimatedBytes;
                    }
                }
                else if (item.SizeBytes <= 0 && item.RegistryEstimatedBytes > 0)
                {
                    item.SizeBytes = item.RegistryEstimatedBytes;
                }
                else if (item.SizeBytes <= 0 && !string.IsNullOrEmpty(item.ExecutableOrIconPath) && File.Exists(item.ExecutableOrIconPath))
                {
                    try
                    {
                        FileInfo fi = new FileInfo(item.ExecutableOrIconPath);
                        item.SizeBytes = fi.Length;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(item.DriveLetter))
                {
                    if (!string.IsNullOrEmpty(item.InstallLocation))
                        item.DriveLetter = FormatUtils.GetDriveLetter(item.InstallLocation);
                    if (string.IsNullOrEmpty(item.DriveLetter) && !string.IsNullOrEmpty(item.ExecutableOrIconPath))
                        item.DriveLetter = FormatUtils.GetDriveLetter(item.ExecutableOrIconPath);
                    if (string.IsNullOrEmpty(item.DriveLetter))
                        item.DriveLetter = "C:";
                }

                // Populate CreatedDate and LastModified from disk folder/file
                try
                {
                    if (!string.IsNullOrEmpty(item.InstallLocation) && Directory.Exists(item.InstallLocation))
                    {
                        item.CreatedDate = Directory.GetCreationTime(item.InstallLocation);
                        item.LastModified = Directory.GetLastWriteTime(item.InstallLocation);
                        if (string.IsNullOrEmpty(item.InstallDate) && item.CreatedDate.Year > 1980)
                        {
                            item.InstallDate = item.CreatedDate.ToString("yyyy-MM-dd");
                        }
                    }
                    else if (!string.IsNullOrEmpty(item.ExecutableOrIconPath) && File.Exists(item.ExecutableOrIconPath))
                    {
                        item.CreatedDate = File.GetCreationTime(item.ExecutableOrIconPath);
                        item.LastModified = File.GetLastWriteTime(item.ExecutableOrIconPath);
                        if (string.IsNullOrEmpty(item.InstallDate) && item.CreatedDate.Year > 1980)
                        {
                            item.InstallDate = item.CreatedDate.ToString("yyyy-MM-dd");
                        }
                    }
                }
                catch { }

                int done = System.Threading.Interlocked.Increment(ref processedCount);
                if (done % 30 == 0 && onStatus != null)
                {
                    onStatus(string.Format("Calculando peso real en disco de instalaciones ({0} / {1})...", done, totalToProcess));
                }
            });

            masterList.Sort(delegate(InstallItem a, InstallItem b)
            {
                int cmp = b.SizeBytes.CompareTo(a.SizeBytes);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return masterList;
        }

        private static void RegisterInstallItem(
            InstallItem item,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;

            string normPath = FormatUtils.NormalizePath(item.InstallLocation);
            string normName = NormalizeNameForDedup(item.Name);

            InstallItem existingByPath = null;
            if (!string.IsNullOrEmpty(normPath))
            {
                byPath.TryGetValue(normPath, out existingByPath);
                if (existingByPath == null)
                {
                    // Also check if an existing item's path is a direct parent or child of normPath
                    foreach (KeyValuePair<string, InstallItem> kv in byPath)
                    {
                        string p = kv.Key;
                        if (!IsGenericContainerPath(p) && !IsGenericContainerPath(normPath))
                        {
                            if (normPath.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                existingByPath = kv.Value;
                                break;
                            }
                        }
                    }
                }
            }

            InstallItem existingByName = null;
            if (!string.IsNullOrEmpty(normName))
            {
                byName.TryGetValue(normName, out existingByName);
            }

            // Only merge by name if they don't point to two completely separate physical folders on disk!
            InstallItem existing = existingByPath;
            if (existing == null && existingByName != null)
            {
                bool bothHaveDistinctFolders = !string.IsNullOrEmpty(normPath) &&
                                               !string.IsNullOrEmpty(existingByName.InstallLocation) &&
                                               !string.Equals(normPath, existingByName.InstallLocation, StringComparison.OrdinalIgnoreCase);
                if (!bothHaveDistinctFolders)
                {
                    existing = existingByName;
                }
            }
            if (existing != null)
            {
                // Merge metadata into existing item
                if (item.SizeBytes > existing.SizeBytes)
                {
                    existing.SizeBytes = item.SizeBytes;
                    if (item.SizeCalculatedFromDisk) existing.SizeCalculatedFromDisk = true;
                }
                if (item.RegistryEstimatedBytes > existing.RegistryEstimatedBytes)
                    existing.RegistryEstimatedBytes = item.RegistryEstimatedBytes;
                if (string.IsNullOrEmpty(existing.Publisher) && !string.IsNullOrEmpty(item.Publisher))
                    existing.Publisher = item.Publisher;
                if (string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(item.Version))
                    existing.Version = item.Version;
                if (string.IsNullOrEmpty(existing.InstallDate) && !string.IsNullOrEmpty(item.InstallDate))
                    existing.InstallDate = item.InstallDate;
                if (string.IsNullOrEmpty(existing.InstallLocation) && !string.IsNullOrEmpty(normPath))
                {
                    existing.InstallLocation = normPath;
                    existing.DriveLetter = FormatUtils.GetDriveLetter(normPath);
                    byPath[normPath] = existing;
                    ClaimPathHierarchy(normPath, claimedPaths);
                }
                return;
            }

            item.InstallLocation = normPath;
            masterList.Add(item);
            if (!string.IsNullOrEmpty(normPath))
            {
                byPath[normPath] = item;
                ClaimPathHierarchy(normPath, claimedPaths);
            }
            if (!string.IsNullOrEmpty(normName))
            {
                byName[normName] = item;
            }
        }

        private static string NormalizeNameForDedup(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            // Remove punctuation like ':' or '-' or spaces so "Detroit: Become Human" matches "Detroit Become Human"
            char[] arr = name.ToLowerInvariant().ToCharArray();
            char[] clean = new char[arr.Length];
            int idx = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                char c = arr[i];
                if (char.IsLetterOrDigit(c))
                {
                    clean[idx++] = c;
                }
            }
            return new string(clean, 0, idx);
        }

        private static void ScanSteamManifests(
            List<DiskItem> disks,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            if (disks == null) return;
            string[] steamAppsRel = new string[]
            {
                "SteamLibrary\\steamapps",
                "Steam\\steamapps",
                "Program Files (x86)\\Steam\\steamapps",
                "Program Files\\Steam\\steamapps",
                "Games\\Steam\\steamapps",
                "Juegos\\Steam\\steamapps"
            };

            foreach (DiskItem disk in disks)
            {
                string root = disk.DriveLetter + "\\";
                foreach (string rel in steamAppsRel)
                {
                    string steamAppsDir = Path.Combine(root, rel);
                    if (!Directory.Exists(steamAppsDir)) continue;

                    try
                    {
                        string[] acfFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf");
                        foreach (string acf in acfFiles)
                        {
                            try
                            {
                                string content = File.ReadAllText(acf);
                                string gameName = ExtractAcfField(content, "name");
                                string installDirName = ExtractAcfField(content, "installdir");
                                string sizeOnDiskStr = ExtractAcfField(content, "SizeOnDisk");

                                if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(installDirName))
                                    continue;

                                string fullGameFolder = FormatUtils.NormalizePath(Path.Combine(Path.Combine(steamAppsDir, "common"), installDirName));
                                if (!Directory.Exists(fullGameFolder))
                                    continue;

                                long sizeOnDisk = 0;
                                long.TryParse(sizeOnDiskStr, out sizeOnDisk);

                                DateTime lastWrite = DateTime.MinValue;
                                try { lastWrite = Directory.GetLastWriteTime(fullGameFolder); } catch { }

                                InstallItem item = new InstallItem();
                                item.Name = gameName;
                                item.Publisher = "Steam";
                                item.InstallDate = lastWrite > DateTime.MinValue ? lastWrite.ToString("yyyy-MM-dd") : "";
                                item.InstallLocation = fullGameFolder;
                                item.DriveLetter = disk.DriveLetter;
                                item.SourceType = "Juego de Steam (" + disk.DriveLetter + ")";
                                item.Category = ItemCategory.Juegos;
                                if (sizeOnDisk > 0)
                                {
                                    item.SizeBytes = sizeOnDisk;
                                    item.RegistryEstimatedBytes = sizeOnDisk;
                                    item.SizeCalculatedFromDisk = true;
                                }

                                RegisterInstallItem(item, masterList, byPath, byName, claimedPaths);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        private static string ExtractAcfField(string content, string key)
        {
            if (string.IsNullOrEmpty(content)) return "";
            string pattern = "\"" + key + "\"\\s+\"([^\"]+)\"";
            Match m = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1)
            {
                return m.Groups[1].Value.Trim();
            }
            return "";
        }

        private static void ScanRegistryHive(
            RegistryHive hive,
            RegistryView view,
            string subKeyPath,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey uninstallKey = baseKey.OpenSubKey(subKeyPath, false))
                {
                    if (uninstallKey == null) return;
                    string[] subKeyNames = uninstallKey.GetSubKeyNames();
                    foreach (string subName in subKeyNames)
                    {
                        try
                        {
                            using (RegistryKey appKey = uninstallKey.OpenSubKey(subName, false))
                            {
                                if (appKey == null) continue;

                                object sysCompObj = appKey.GetValue("SystemComponent");
                                if (sysCompObj != null && Convert.ToInt32(sysCompObj) == 1)
                                    continue;

                                string releaseType = Convert.ToString(appKey.GetValue("ReleaseType") ?? "");
                                if (releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase) ||
                                    releaseType.Equals("Update Rollup", StringComparison.OrdinalIgnoreCase) ||
                                    releaseType.Equals("Hotfix", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string parentKeyName = Convert.ToString(appKey.GetValue("ParentKeyName") ?? "");
                                if (!string.IsNullOrEmpty(parentKeyName))
                                    continue;

                                string displayName = Convert.ToString(appKey.GetValue("DisplayName") ?? "").Trim();
                                if (string.IsNullOrEmpty(displayName))
                                    continue;

                                string publisher = Convert.ToString(appKey.GetValue("Publisher") ?? "").Trim();
                                string displayVersion = Convert.ToString(appKey.GetValue("DisplayVersion") ?? "").Trim();
                                string installDate = FormatRegistryDate(Convert.ToString(appKey.GetValue("InstallDate") ?? "").Trim());
                                string installLocation = FormatUtils.NormalizePath(Convert.ToString(appKey.GetValue("InstallLocation") ?? ""));
                                string displayIcon = Convert.ToString(appKey.GetValue("DisplayIcon") ?? "").Trim();
                                string uninstallString = Convert.ToString(appKey.GetValue("UninstallString") ?? "").Trim();

                                long estimatedBytes = 0;
                                object estObj = appKey.GetValue("EstimatedSize");
                                if (estObj != null)
                                {
                                    long kb = Convert.ToInt64(estObj);
                                    if (kb > 0) estimatedBytes = kb * 1024L;
                                }

                                string exeOrIconPath = ExtractFilePathFromCommand(displayIcon);
                                string resolvedFolder = "";

                                if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation) && !IsGenericContainerPath(installLocation))
                                {
                                    resolvedFolder = NormalizeAppRootFolder(installLocation);
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(exeOrIconPath) && File.Exists(exeOrIconPath))
                                    {
                                        string dir = FormatUtils.NormalizePath(Path.GetDirectoryName(exeOrIconPath));
                                        if (!IsGenericContainerPath(dir))
                                        {
                                            resolvedFolder = NormalizeAppRootFolder(dir);
                                        }
                                    }

                                    if (string.IsNullOrEmpty(resolvedFolder) && !string.IsNullOrEmpty(uninstallString))
                                    {
                                        string uninstExe = ExtractFilePathFromCommand(uninstallString);
                                        if (!string.IsNullOrEmpty(uninstExe) && File.Exists(uninstExe))
                                        {
                                            string dir = FormatUtils.NormalizePath(Path.GetDirectoryName(uninstExe));
                                            if (!IsGenericContainerPath(dir))
                                            {
                                                resolvedFolder = NormalizeAppRootFolder(dir);
                                            }
                                        }
                                    }
                                }

                                string sourceType = (hive == RegistryHive.CurrentUser)
                                    ? "Instalación Usuario (Registro)"
                                    : "Instalación Sistema (Registro)";

                                ItemCategory cat = ItemCategory.Instalacion;
                                if (subName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) ||
                                    (!string.IsNullOrEmpty(resolvedFolder) && resolvedFolder.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                    (!string.IsNullOrEmpty(resolvedFolder) && resolvedFolder.IndexOf("Epic Games", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                    (!string.IsNullOrEmpty(resolvedFolder) && resolvedFolder.IndexOf("XboxGames", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                    (!string.IsNullOrEmpty(resolvedFolder) && resolvedFolder.IndexOf("Riot Games", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    sourceType = "Juego Instalado";
                                    cat = ItemCategory.Juegos;
                                }

                                string driveLetter = FormatUtils.GetDriveLetter(resolvedFolder);
                                if (string.IsNullOrEmpty(driveLetter))
                                    driveLetter = FormatUtils.GetDriveLetter(exeOrIconPath);
                                if (string.IsNullOrEmpty(driveLetter))
                                    driveLetter = "C:";

                                InstallItem item = new InstallItem();
                                item.Name = displayName;
                                item.Publisher = publisher;
                                item.Version = displayVersion;
                                item.InstallDate = installDate;
                                item.InstallLocation = resolvedFolder;
                                item.ExecutableOrIconPath = exeOrIconPath;
                                item.UninstallString = uninstallString;
                                item.DriveLetter = driveLetter;
                                item.SourceType = sourceType;
                                item.RegistryEstimatedBytes = estimatedBytes;
                                item.SizeBytes = estimatedBytes;
                                item.Category = cat;

                                RegisterInstallItem(item, masterList, byPath, byName, claimedPaths);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void ScanAppXPackages(
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            try
            {
                string keyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
                using (RegistryKey packagesKey = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (packagesKey == null) return;
                    foreach (string pkgFullName in packagesKey.GetSubKeyNames())
                    {
                        try
                        {
                            if (pkgFullName.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.Services.Store", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.DirectXRuntime", StringComparison.OrdinalIgnoreCase) ||
                                pkgFullName.StartsWith("Microsoft.Advertising", StringComparison.OrdinalIgnoreCase))
                                continue;

                            using (RegistryKey pkgKey = packagesKey.OpenSubKey(pkgFullName, false))
                            {
                                if (pkgKey == null) continue;
                                string rootFolder = FormatUtils.NormalizePath(Convert.ToString(pkgKey.GetValue("PackageRootFolder") ?? ""));
                                if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
                                    continue;

                                string displayName = Convert.ToString(pkgKey.GetValue("DisplayName") ?? "");
                                string friendlyName = CleanAppXName(pkgFullName, displayName);
                                if (string.IsNullOrEmpty(friendlyName)) continue;

                                string drive = FormatUtils.GetDriveLetter(rootFolder);
                                if (string.IsNullOrEmpty(drive)) drive = "C:";

                                if (IsPathAlreadyClaimed(rootFolder, claimedPaths))
                                    continue;

                                InstallItem item = new InstallItem();
                                item.Name = friendlyName;
                                item.Publisher = "Microsoft Store / AppX";
                                item.Version = ExtractAppXVersion(pkgFullName);
                                item.InstallLocation = rootFolder;
                                item.DriveLetter = drive;
                                item.SourceType = "Microsoft Store / App";
                                item.Category = ItemCategory.Instalacion;

                                RegisterInstallItem(item, masterList, byPath, byName, claimedPaths);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void ScanDisksForProgramsAndGames(
            List<DiskItem> disks,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            if (disks == null) return;

            string[] gameLibraryRelativePaths = new string[]
            {
                "SteamLibrary\\steamapps\\common",
                "Steam\\steamapps\\common",
                "Program Files (x86)\\Steam\\steamapps\\common",
                "Program Files\\Steam\\steamapps\\common",
                "Epic Games",
                "Program Files\\Epic Games",
                "XboxGames",
                "Riot Games",
                "Ubisoft\\Ubisoft Game Launcher\\games",
                "Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\games",
                "EA Games",
                "Program Files\\EA Games",
                "Origin Games",
                "Program Files (x86)\\Origin Games",
                "GOG Galaxy\\Games",
                "Program Files (x86)\\GOG Galaxy\\Games",
                "GOG Games",
                "Games",
                "Juegos"
            };

            string[] programRelativePaths = new string[]
            {
                "Program Files",
                "Program Files (x86)",
                "Programas",
                "Programs",
                "Apps",
                "Portable"
            };

            foreach (DiskItem disk in disks)
            {
                string root = disk.DriveLetter + "\\";

                foreach (string rel in gameLibraryRelativePaths)
                {
                    string libDir = Path.Combine(root, rel);
                    if (Directory.Exists(libDir))
                    {
                        ScanImmediateSubfoldersAsInstallations(
                            libDir,
                            disk.DriveLetter,
                            "Juego / Librería en Disco",
                            "Juego / Launcher",
                            ItemCategory.Juegos,
                            false,
                            masterList,
                            byPath,
                            byName,
                            claimedPaths);
                    }
                }

                foreach (string rel in programRelativePaths)
                {
                    string progDir = Path.Combine(root, rel);
                    if (Directory.Exists(progDir))
                    {
                        ScanImmediateSubfoldersAsInstallations(
                            progDir,
                            disk.DriveLetter,
                            "Carpeta de Programa (" + rel + ")",
                            "Programa en Disco",
                            ItemCategory.Instalacion,
                            true,
                            masterList,
                            byPath,
                            byName,
                            claimedPaths);
                    }
                }

                if (!disk.DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase))
                {
                    ScanSecondaryDriveRootFolders(disk.DriveLetter, masterList, byPath, byName, claimedPaths);
                }
            }

            // Scan User AppData\Local\Programs
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string localPrograms = Path.Combine(localAppData, "Programs");
                    if (Directory.Exists(localPrograms))
                    {
                        ScanImmediateSubfoldersAsInstallations(
                            localPrograms,
                            FormatUtils.GetDriveLetter(localPrograms),
                            "AppData\\Local\\Programs",
                            "App de Usuario",
                            ItemCategory.Instalacion,
                            false,
                            masterList,
                            byPath,
                            byName,
                            claimedPaths);
                    }
                }
            }
            catch { }
        }

        private static void ScanImmediateSubfoldersAsInstallations(
            string containerPath,
            string driveLetter,
            string sourceTypeLabel,
            string defaultPublisher,
            ItemCategory category,
            bool requireExe,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            try
            {
                string[] subDirs = Directory.GetDirectories(containerPath);
                foreach (string rawSubDir in subDirs)
                {
                    string subDir = FormatUtils.NormalizePath(rawSubDir);
                    string folderName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(folderName)) continue;

                    string lowerName = folderName.ToLowerInvariant();
                    if (lowerName == "common files" || lowerName == "windowsapps" ||
                        lowerName == "modifiablewindowsapps" || lowerName == "uninstall information" ||
                        lowerName == "temp" || lowerName == "cache" || lowerName == "caches" ||
                        lowerName == "crashdumps" || lowerName == "connecteddevicesplatform" ||
                        lowerName == "d3dscache" || lowerName == "packages" || lowerName == "virtualstore" ||
                        lowerName.StartsWith("$") || lowerName == "system volume information")
                        continue;

                    if (IsPathAlreadyClaimed(subDir, claimedPaths))
                        continue;

                    bool hasExe = DirectoryContainsExeFast(subDir, 2);
                    if (requireExe && !hasExe)
                        continue;

                    DateTime lastWrite = DateTime.MinValue;
                    try { lastWrite = Directory.GetLastWriteTime(subDir); } catch { }

                    InstallItem item = new InstallItem();
                    item.Name = folderName;
                    item.Publisher = hasExe ? (defaultPublisher + " (.exe)") : defaultPublisher;
                    item.InstallDate = lastWrite > DateTime.MinValue ? lastWrite.ToString("yyyy-MM-dd") : "";
                    item.InstallLocation = subDir;
                    item.DriveLetter = string.IsNullOrEmpty(driveLetter) ? FormatUtils.GetDriveLetter(subDir) : driveLetter;
                    item.SourceType = sourceTypeLabel;
                    item.Category = category;

                    RegisterInstallItem(item, masterList, byPath, byName, claimedPaths);
                }
            }
            catch { }
        }

        private static void ScanSecondaryDriveRootFolders(
            string driveLetter,
            List<InstallItem> masterList,
            Dictionary<string, InstallItem> byPath,
            Dictionary<string, InstallItem> byName,
            HashSet<string> claimedPaths)
        {
            try
            {
                string root = driveLetter + "\\";
                string[] topDirs = Directory.GetDirectories(root);
                foreach (string rawDir in topDirs)
                {
                    string dir = FormatUtils.NormalizePath(rawDir);
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;

                    string lower = name.ToLowerInvariant();
                    if (lower.StartsWith("$") || lower == "system volume information" ||
                        lower == "recovery" || lower == "windows" || lower == "msocache" ||
                        lower == "config.msi" || IsGenericContainerPath(dir))
                        continue;

                    if (IsPathAlreadyClaimed(dir, claimedPaths))
                        continue;

                    if (DirectoryContainsExeFast(dir, 2))
                    {
                        DateTime lastWrite = DateTime.MinValue;
                        try { lastWrite = Directory.GetLastWriteTime(dir); } catch { }

                        InstallItem item = new InstallItem();
                        item.Name = name;
                        item.Publisher = "Aplicación / Juego en " + driveLetter;
                        item.InstallDate = lastWrite > DateTime.MinValue ? lastWrite.ToString("yyyy-MM-dd") : "";
                        item.InstallLocation = dir;
                        item.DriveLetter = driveLetter;
                        item.SourceType = "Portable / Carpeta con .exe (" + driveLetter + ")";
                        item.Category = ItemCategory.Instalacion;

                        RegisterInstallItem(item, masterList, byPath, byName, claimedPaths);
                    }
                }
            }
            catch { }
        }

        private static bool DirectoryContainsExeFast(string folderPath, int maxDepth)
        {
            try
            {
                string[] files = Directory.GetFiles(folderPath, "*.exe");
                if (files != null && files.Length > 0) return true;
                if (maxDepth <= 0) return false;

                string[] subDirs = Directory.GetDirectories(folderPath);
                int checkedCount = 0;
                foreach (string sub in subDirs)
                {
                    checkedCount++;
                    if (checkedCount > 20) break;
                    if (DirectoryContainsExeFast(sub, maxDepth - 1))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void ClaimPathHierarchy(string folderPath, HashSet<string> claimedPaths)
        {
            string norm = FormatUtils.NormalizePath(folderPath);
            if (!string.IsNullOrEmpty(norm))
            {
                claimedPaths.Add(norm);
            }
        }

        private static bool IsPathAlreadyClaimed(string folderPath, HashSet<string> claimedPaths)
        {
            string norm = FormatUtils.NormalizePath(folderPath);
            if (string.IsNullOrEmpty(norm)) return false;
            if (claimedPaths.Contains(norm)) return true;

            foreach (string claimed in claimedPaths)
            {
                if (!IsGenericContainerPath(claimed) && norm.StartsWith(claimed + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!IsGenericContainerPath(norm) && claimed.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string NormalizeAppRootFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            string norm = FormatUtils.NormalizePath(dir);
            string leaf = Path.GetFileName(norm);
            if (!string.IsNullOrEmpty(leaf))
            {
                string lowerLeaf = leaf.ToLowerInvariant();
                if (lowerLeaf == "bin" || lowerLeaf == "binaries" || lowerLeaf == "x64" ||
                    lowerLeaf == "x86" || lowerLeaf == "win64" || lowerLeaf == "win32" ||
                    lowerLeaf == "client" || lowerLeaf == "app" || lowerLeaf == "live" ||
                    lowerLeaf == "retail" || lowerLeaf == "_retail_")
                {
                    string parent = FormatUtils.NormalizePath(Path.GetDirectoryName(norm));
                    if (!string.IsNullOrEmpty(parent) && !IsGenericContainerPath(parent))
                        return parent;
                }
            }
            return norm;
        }

        private static string ExtractFilePathFromCommand(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return "";
            string s = rawCommand.Trim();

            int commaIdx = s.LastIndexOf(',');
            if (commaIdx > 3)
            {
                string afterComma = s.Substring(commaIdx + 1).Trim();
                int dummy;
                if (int.TryParse(afterComma, out dummy))
                {
                    s = s.Substring(0, commaIdx).Trim();
                }
            }

            if (s.StartsWith("\""))
            {
                int endQuote = s.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    s = s.Substring(1, endQuote - 1);
                }
            }
            else
            {
                int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0)
                {
                    s = s.Substring(0, exeIdx + 4);
                }
                else
                {
                    int spaceIdx = s.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        s = s.Substring(0, spaceIdx);
                    }
                }
            }

            s = s.Trim().Trim('"');
            if (s.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("rundll32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("\\Windows\\System32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("\\Windows\\SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "";
            }
            return s;
        }

        private static string FormatRegistryDate(string rawDate)
        {
            if (string.IsNullOrEmpty(rawDate)) return "";
            if (rawDate.Length == 8)
            {
                return string.Format("{0}-{1}-{2}",
                    rawDate.Substring(0, 4),
                    rawDate.Substring(4, 2),
                    rawDate.Substring(6, 2));
            }
            return rawDate;
        }

        private static string CleanAppXName(string pkgFullName, string displayName)
        {
            if (!string.IsNullOrEmpty(displayName) &&
                !displayName.StartsWith("@", StringComparison.Ordinal) &&
                !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }
            int underscore = pkgFullName.IndexOf('_');
            string baseName = underscore > 0 ? pkgFullName.Substring(0, underscore) : pkgFullName;
            if (baseName.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.BioEnrollment", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.AAD.", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.AccountsControl", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.AsyncTextService", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.CredDialogHost", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.ECApp", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.LockApp", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Microsoft.Win32WebViewHost", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            if (baseName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring("Microsoft.".Length);
            }
            return baseName;
        }

        private static string ExtractAppXVersion(string pkgFullName)
        {
            string[] parts = pkgFullName.Split('_');
            if (parts.Length >= 2) return parts[1];
            return "";
        }

        public static void LinkInstallationsWithScannedDisk(DiskItem disk, List<InstallItem> installations)
        {
            if (disk == null || disk.RootNode == null || installations == null) return;

            Dictionary<string, FsNode> folderMap = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase);
            Queue<KeyValuePair<FsNode, int>> q = new Queue<KeyValuePair<FsNode, int>>();
            q.Enqueue(new KeyValuePair<FsNode, int>(disk.RootNode, 0));

            while (q.Count > 0)
            {
                KeyValuePair<FsNode, int> kv = q.Dequeue();
                FsNode node = kv.Key;
                int depth = kv.Value;

                string normPath = FormatUtils.NormalizePath(node.FullPath);
                if (!string.IsNullOrEmpty(normPath))
                {
                    folderMap[normPath] = node;
                }

                if (depth < 5 && node.Children != null)
                {
                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        if (node.Children[i].IsFolder)
                        {
                            q.Enqueue(new KeyValuePair<FsNode, int>(node.Children[i], depth + 1));
                        }
                    }
                }
            }

            for (int i = 0; i < installations.Count; i++)
            {
                InstallItem inst = installations[i];
                if (!string.Equals(inst.DriveLetter, disk.DriveLetter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(inst.InstallLocation))
                {
                    string norm = FormatUtils.NormalizePath(inst.InstallLocation);
                    FsNode matched;
                    if (folderMap.TryGetValue(norm, out matched))
                    {
                        inst.LinkedNode = matched;
                        matched.MatchedInstall = inst;
                        matched.Category = inst.Category;
                        if (matched.Size > 0)
                        {
                            inst.SizeBytes = Math.Max(matched.Size, inst.RegistryEstimatedBytes);
                            inst.SizeCalculatedFromDisk = true;
                        }
                    }
                }
            }
        }
    }
}
