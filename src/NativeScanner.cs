using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AnalizadorDiscosPortable
{
    public static class NativeScanner
    {
        private const int FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const int FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1
        }

        private enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch = 0
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public int dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public int dwReserved0;
            public int dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATAW lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        public static List<DiskItem> DetectAllDisks()
        {
            List<DiskItem> list = new List<DiskItem>();
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo d in drives)
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        DiskItem item = new DiskItem();
                        item.Name = d.Name;
                        item.DriveLetter = d.Name.TrimEnd('\\');
                        item.VolumeLabel = d.VolumeLabel;
                        item.DriveFormat = d.DriveFormat;
                        item.DriveTypeDesc = TranslateDriveType(d.DriveType);
                        item.TotalBytes = d.TotalSize;
                        item.FreeBytes = d.TotalFreeSpace;
                        item.UsedBytes = Math.Max(0L, item.TotalBytes - item.FreeBytes);
                        item.UsedPercent = item.TotalBytes > 0
                            ? ((double)item.UsedBytes * 100.0 / (double)item.TotalBytes)
                            : 0.0;
                        list.Add(item);
                    }
                    catch
                    {
                        // Ignore inaccessible drive
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        private static string TranslateDriveType(DriveType dt)
        {
            switch (dt)
            {
                case DriveType.Fixed: return "Disco Local";
                case DriveType.Removable: return "Extraíble / USB";
                case DriveType.Network: return "Unidad de Red";
                case DriveType.CDRom: return "Unidad Óptica";
                case DriveType.Ram: return "Disco RAM";
                default: return "Disco";
            }
        }

        private static string ToLongPathPattern(string folderPath)
        {
            string clean = folderPath;
            if (!clean.EndsWith("\\")) clean += "\\";
            if (clean.StartsWith("\\\\?\\"))
                return clean + "*";
            if (clean.StartsWith("\\\\"))
                return "\\\\?\\UNC\\" + clean.Substring(2) + "*";
            return "\\\\?\\" + clean + "*";
        }

        private static DateTime FileTimeToDateTime(FILETIME ft)
        {
            try
            {
                long hFT = (((long)ft.dwHighDateTime) << 32) | ((uint)ft.dwLowDateTime);
                if (hFT <= 0) return DateTime.MinValue;
                return DateTime.FromFileTimeUtc(hFT).ToLocalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static void ScanDisk(
            DiskItem disk,
            Func<bool> shouldCancel,
            Action<string, long, long, long> onProgress)
        {
            if (disk == null) return;
            disk.IsScanning = true;
            disk.ScannedFilesCount = 0;
            disk.ScannedFoldersCount = 0;
            disk.ScannedBytes = 0;

            FsNode root = new FsNode();
            root.Name = disk.DriveLetter + "\\";
            root.FullPath = disk.DriveLetter + "\\";
            root.DriveLetter = disk.DriveLetter;
            root.IsFolder = true;
            root.Category = ItemCategory.CarpetaGeneral;

            ConcurrentQueue<FsNode> queue = new ConcurrentQueue<FsNode>();
            List<FsNode> allDirectories = new List<FsNode>(32768);
            object allDirsLock = new object();

            List<FsNode> topFiles = new List<FsNode>(600);
            object topFilesLock = new object();
            long minTopFileSize = 1024 * 1024; // Only track files >= 1 MB in TopFiles initially

            queue.Enqueue(root);
            allDirectories.Add(root);

            int activeWorkers = 0;
            long filesScanned = 0;
            long foldersScanned = 1;
            long bytesScanned = 0;
            string currentPathSample = root.FullPath;
            int lastTick = Environment.TickCount;

            int workerCount = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            Thread[] workers = new Thread[workerCount];
            ManualResetEvent doneEvent = new ManualResetEvent(false);

            for (int i = 0; i < workerCount; i++)
            {
                workers[i] = new Thread(delegate()
                {
                    while (true)
                    {
                        if (shouldCancel != null && shouldCancel())
                            break;

                        FsNode dirNode;
                        if (queue.TryDequeue(out dirNode))
                        {
                            Interlocked.Increment(ref activeWorkers);
                            try
                            {
                                currentPathSample = dirNode.FullPath;
                                ScanSingleDirectory(
                                    dirNode,
                                    disk.DriveLetter,
                                    queue,
                                    allDirectories,
                                    allDirsLock,
                                    topFiles,
                                    topFilesLock,
                                    ref minTopFileSize,
                                    ref filesScanned,
                                    ref foldersScanned,
                                    ref bytesScanned);
                            }
                            catch
                            {
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeWorkers);
                            }

                            int now = Environment.TickCount;
                            if (now - lastTick > 120)
                            {
                                lastTick = now;
                                disk.ScannedFilesCount = Interlocked.Read(ref filesScanned);
                                disk.ScannedFoldersCount = Interlocked.Read(ref foldersScanned);
                                disk.ScannedBytes = Interlocked.Read(ref bytesScanned);
                                if (onProgress != null)
                                {
                                    onProgress(currentPathSample, disk.ScannedFilesCount, disk.ScannedFoldersCount, disk.ScannedBytes);
                                }
                            }
                        }
                        else
                        {
                            if (Volatile.Read(ref activeWorkers) == 0 && queue.IsEmpty)
                            {
                                break;
                            }
                            Thread.Sleep(5);
                        }
                    }
                });
                workers[i].IsBackground = true;
                workers[i].Start();
            }

            for (int i = 0; i < workerCount; i++)
            {
                workers[i].Join();
            }

            // Bottom-up size aggregation: iterate allDirectories in reverse order
            // Wait: because multi-threaded enqueue isn't strictly topological when one branch is fast,
            // let's compute folder sizes via a clean post-order traversal using an explicit stack!
            ComputeSizesBottomUp(root);

            // Check if there is unenumerated protected OS space (e.g., System Volume Information / Shadow Copies / NTFS MFT)
            try
            {
                DriveInfo di = new DriveInfo(disk.DriveLetter);
                if (di.IsReady)
                {
                    disk.TotalBytes = di.TotalSize;
                    disk.FreeBytes = di.TotalFreeSpace;
                    disk.UsedBytes = Math.Max(0L, disk.TotalBytes - disk.FreeBytes);
                    disk.UsedPercent = disk.TotalBytes > 0 ? ((double)disk.UsedBytes * 100.0 / (double)disk.TotalBytes) : 0.0;
                }
            }
            catch
            {
            }

            long unaccounted = disk.UsedBytes - root.Size;
            if (unaccounted > 150L * 1024L * 1024L && (shouldCancel == null || !shouldCancel()))
            {
                FsNode sysProtected = new FsNode();
                sysProtected.Name = "[Espacio Protegido del Sistema / Restauración / MFT]";
                sysProtected.FullPath = disk.DriveLetter + "\\System Volume Information";
                sysProtected.DriveLetter = disk.DriveLetter;
                sysProtected.IsFolder = false;
                sysProtected.Size = unaccounted;
                sysProtected.FileCount = 1;
                sysProtected.Extension = ".sys";
                sysProtected.Category = ItemCategory.SistemaWindows;
                sysProtected.Parent = root;
                root.Children.Add(sysProtected);
                root.Size += unaccounted;
                root.SortChildrenBySize();
            }

            topFiles.Sort(delegate(FsNode a, FsNode b)
            {
                return b.Size.CompareTo(a.Size);
            });
            if (topFiles.Count > 500)
            {
                topFiles.RemoveRange(500, topFiles.Count - 500);
            }

            disk.RootNode = root;
            disk.TopFiles = topFiles;
            disk.ScannedFilesCount = filesScanned;
            disk.ScannedFoldersCount = foldersScanned;
            disk.ScannedBytes = root.Size;
            disk.IsScanned = true;
            disk.IsScanning = false;
        }

        private static void ScanSingleDirectory(
            FsNode dirNode,
            string driveLetter,
            ConcurrentQueue<FsNode> queue,
            List<FsNode> allDirectories,
            object allDirsLock,
            List<FsNode> topFiles,
            object topFilesLock,
            ref long minTopFileSize,
            ref long filesScanned,
            ref long foldersScanned,
            ref long bytesScanned)
        {
            WIN32_FIND_DATAW findData;
            string pattern = ToLongPathPattern(dirNode.FullPath);
            IntPtr hFind = FindFirstFileExW(
                pattern,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                out findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (hFind == INVALID_HANDLE_VALUE)
                return;

            List<FsNode> localSubDirs = new List<FsNode>();
            List<FsNode> localFiles = new List<FsNode>();
            long localFilesBytes = 0;
            int localFileCount = 0;

            string basePath = dirNode.FullPath;
            if (!basePath.EndsWith("\\")) basePath += "\\";

            try
            {
                do
                {
                    string name = findData.cFileName;
                    if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                        continue;

                    int attr = findData.dwFileAttributes;
                    bool isDir = (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    bool isReparse = (attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                    if (isDir)
                    {
                        if (isReparse) continue; // Skip symlinks and junction loops

                        FsNode subDir = new FsNode();
                        subDir.Name = name;
                        subDir.FullPath = basePath + name;
                        subDir.DriveLetter = driveLetter;
                        subDir.IsFolder = true;
                        subDir.CreatedDate = FileTimeToDateTime(findData.ftCreationTime);
                        subDir.LastModified = FileTimeToDateTime(findData.ftLastWriteTime);
                        subDir.Category = FormatUtils.ClassifyFolder(name, subDir.FullPath, null);
                        subDir.Parent = dirNode;

                        localSubDirs.Add(subDir);
                    }
                    else
                    {
                        long fileSize = (((long)findData.nFileSizeHigh) << 32) | ((long)findData.nFileSizeLow & 0xFFFFFFFFL);
                        if (fileSize < 0) fileSize = 0;

                        localFilesBytes += fileSize;
                        localFileCount++;

                        string ext = "";
                        int dotIdx = name.LastIndexOf('.');
                        if (dotIdx >= 0 && dotIdx < name.Length - 1)
                        {
                            ext = name.Substring(dotIdx);
                        }

                        FsNode fileNode = new FsNode();
                        fileNode.Name = name;
                        fileNode.FullPath = basePath + name;
                        fileNode.DriveLetter = driveLetter;
                        fileNode.IsFolder = false;
                        fileNode.Size = fileSize;
                        fileNode.OwnFilesSize = fileSize;
                        fileNode.FileCount = 1;
                        fileNode.Extension = ext;
                        fileNode.CreatedDate = FileTimeToDateTime(findData.ftCreationTime);
                        fileNode.LastModified = FileTimeToDateTime(findData.ftLastWriteTime);
                        fileNode.Category = FormatUtils.ClassifyFile(name, ext, fileNode.FullPath);
                        fileNode.Parent = dirNode;

                        localFiles.Add(fileNode);

                        if (fileSize >= Volatile.Read(ref minTopFileSize))
                        {
                            lock (topFilesLock)
                            {
                                if (topFiles.Count < 550)
                                {
                                    topFiles.Add(fileNode);
                                }
                                else if (fileSize > minTopFileSize)
                                {
                                    topFiles.Add(fileNode);
                                    if (topFiles.Count >= 650)
                                    {
                                        topFiles.Sort(delegate(FsNode a, FsNode b) { return b.Size.CompareTo(a.Size); });
                                        topFiles.RemoveRange(500, topFiles.Count - 500);
                                        minTopFileSize = topFiles[topFiles.Count - 1].Size;
                                    }
                                }
                            }
                        }
                    }
                }
                while (FindNextFileW(hFind, out findData));
            }
            finally
            {
                FindClose(hFind);
            }

            dirNode.OwnFilesSize = localFilesBytes;

            // Keep all subdirectories and up to top 250 files directly in Children (and group excess tiny files if >250 in one folder)
            if (localFiles.Count > 250)
            {
                localFiles.Sort(delegate(FsNode a, FsNode b) { return b.Size.CompareTo(a.Size); });
                long groupedSize = 0;
                int groupedCount = 0;
                for (int i = 200; i < localFiles.Count; i++)
                {
                    groupedSize += localFiles[i].Size;
                    groupedCount++;
                }
                localFiles.RemoveRange(200, localFiles.Count - 200);
                if (groupedCount > 0)
                {
                    FsNode groupNode = new FsNode();
                    groupNode.Name = string.Format("[+{0} archivos menores en esta carpeta]", groupedCount);
                    groupNode.FullPath = dirNode.FullPath;
                    groupNode.DriveLetter = driveLetter;
                    groupNode.IsFolder = false;
                    groupNode.Size = groupedSize;
                    groupNode.OwnFilesSize = groupedSize;
                    groupNode.FileCount = groupedCount;
                    groupNode.Extension = ".*";
                    groupNode.Category = ItemCategory.OtrosArchivos;
                    groupNode.Parent = dirNode;
                    localFiles.Add(groupNode);
                }
            }

            dirNode.Children.Capacity = localSubDirs.Count + localFiles.Count;
            dirNode.Children.AddRange(localSubDirs);
            dirNode.Children.AddRange(localFiles);

            if (localFileCount > 0)
            {
                Interlocked.Add(ref filesScanned, localFileCount);
                Interlocked.Add(ref bytesScanned, localFilesBytes);
            }

            if (localSubDirs.Count > 0)
            {
                Interlocked.Add(ref foldersScanned, localSubDirs.Count);
                lock (allDirsLock)
                {
                    allDirectories.AddRange(localSubDirs);
                }
                for (int i = 0; i < localSubDirs.Count; i++)
                {
                    queue.Enqueue(localSubDirs[i]);
                }
            }
        }

        private static void ComputeSizesBottomUp(FsNode root)
        {
            if (root == null) return;

            // Post-order traversal using two stacks
            Stack<FsNode> s1 = new Stack<FsNode>(4096);
            Stack<FsNode> s2 = new Stack<FsNode>(32768);
            s1.Push(root);

            while (s1.Count > 0)
            {
                FsNode curr = s1.Pop();
                s2.Push(curr);
                List<FsNode> children = curr.Children;
                if (children != null)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        if (children[i].IsFolder)
                        {
                            s1.Push(children[i]);
                        }
                    }
                }
            }

            while (s2.Count > 0)
            {
                FsNode folder = s2.Pop();
                long totalSize = 0;
                int totalFiles = 0;
                int totalFolders = 0;

                List<FsNode> children = folder.Children;
                if (children != null)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        FsNode child = children[i];
                        totalSize += child.Size;
                        totalFiles += child.FileCount;
                        if (child.IsFolder)
                        {
                            totalFolders += 1 + child.FolderCount;
                        }
                    }
                    folder.SortChildrenBySize();
                }

                folder.Size = totalSize;
                folder.FileCount = totalFiles;
                folder.FolderCount = totalFolders;
            }
        }

        public static long CalculateDirectorySizeFast(string folderPath, int maxDepth, int maxDirectories)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return 0;
            if (!Directory.Exists(folderPath)) return 0;

            long totalBytes = 0;
            int dirsVisited = 0;

            Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
            q.Enqueue(new KeyValuePair<string, int>(folderPath, 0));

            while (q.Count > 0)
            {
                KeyValuePair<string, int> item = q.Dequeue();
                string currentDir = item.Key;
                int depth = item.Value;
                dirsVisited++;
                if (dirsVisited > maxDirectories) break;

                WIN32_FIND_DATAW findData;
                IntPtr hFind = FindFirstFileExW(
                    ToLongPathPattern(currentDir),
                    FINDEX_INFO_LEVELS.FindExInfoBasic,
                    out findData,
                    FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                    IntPtr.Zero,
                    FIND_FIRST_EX_LARGE_FETCH);

                if (hFind == INVALID_HANDLE_VALUE) continue;

                string basePath = currentDir.EndsWith("\\") ? currentDir : (currentDir + "\\");
                try
                {
                    do
                    {
                        string name = findData.cFileName;
                        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                            continue;

                        int attr = findData.dwFileAttributes;
                        bool isDir = (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
                        bool isReparse = (attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                        if (isDir)
                        {
                            if (!isReparse && depth < maxDepth)
                            {
                                q.Enqueue(new KeyValuePair<string, int>(basePath + name, depth + 1));
                            }
                        }
                        else
                        {
                            long fileSize = (((long)findData.nFileSizeHigh) << 32) | ((long)findData.nFileSizeLow & 0xFFFFFFFFL);
                            if (fileSize > 0) totalBytes += fileSize;
                        }
                    }
                    while (FindNextFileW(hFind, out findData));
                }
                finally
                {
                    FindClose(hFind);
                }
            }

            return totalBytes;
        }
    }
}
