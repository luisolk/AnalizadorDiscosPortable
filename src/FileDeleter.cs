using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AnalizadorDiscosPortable
{
    public static class FileDeleter
    {
        private const int FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public int wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        public static bool IsCriticalOperatingSystemRoot(string rawPath)
        {
            string p = FormatUtils.NormalizePath(rawPath);
            if (string.IsNullOrEmpty(p)) return true;
            if (p.Length <= 3) return true; // Drive root C:\, D:\, L:\

            string lower = p.ToLowerInvariant();
            if (lower == "c:\\windows" ||
                lower == "c:\\windows\\system32" ||
                lower == "c:\\windows\\syswow64" ||
                lower == "c:\\windows\\winsxs" ||
                lower == "c:\\users" ||
                lower == "c:\\program files" ||
                lower == "c:\\program files (x86)")
            {
                return true;
            }
            return false;
        }

        public static bool SendToRecycleBin(string path, out string errorMessage)
        {
            errorMessage = "";
            string norm = FormatUtils.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                errorMessage = "Ruta vacía o no válida.";
                return false;
            }

            if (!File.Exists(norm) && !Directory.Exists(norm))
            {
                errorMessage = "El archivo o carpeta ya no existe en el disco.";
                return false;
            }

            try
            {
                SHFILEOPSTRUCTW op = new SHFILEOPSTRUCTW();
                op.wFunc = FO_DELETE;
                op.pFrom = norm + "\0\0";
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT);

                int res = SHFileOperationW(ref op);
                if (res != 0 || op.fAnyOperationsAborted)
                {
                    errorMessage = string.Format("Windows no pudo mover el elemento a la Papelera (Código {0}). Si es demasiado grande o está protegido, usa 'Eliminar Permanentemente (Forzado Admin)'.", res);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool ForceDeletePermanentlyAsAdmin(string path, Action<string> onProgress, out string errorMessage)
        {
            errorMessage = "";
            string norm = FormatUtils.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                errorMessage = "Ruta vacía o no válida.";
                return false;
            }

            bool isFile = File.Exists(norm);
            bool isDir = Directory.Exists(norm);

            if (!isFile && !isDir)
            {
                errorMessage = "El archivo o carpeta ya no existe en el disco.";
                return false;
            }

            try
            {
                if (isFile)
                {
                    if (onProgress != null) onProgress("Eliminando archivo: " + norm);
                    ClearAttributes(norm);
                    try
                    {
                        File.Delete(norm);
                    }
                    catch
                    {
                        TakeOwnershipAndGrantAdmin(norm, false);
                        ClearAttributes(norm);
                        File.Delete(norm);
                    }
                    return !File.Exists(norm);
                }
                else
                {
                    if (onProgress != null) onProgress("Preparando permisos de administrador para borrar carpeta: " + norm);
                    try
                    {
                        DeleteDirectoryRecursiveFast(norm, onProgress);
                    }
                    catch
                    {
                        if (onProgress != null) onProgress("Aplicando permisos forzados de Administrador (takeown / icacls)...");
                        TakeOwnershipAndGrantAdmin(norm, true);
                        DeleteDirectoryRecursiveFast(norm, onProgress);
                    }

                    if (Directory.Exists(norm))
                    {
                        // Final fallback using cmd.exe rd /s /q as Administrator
                        RunHiddenCommand("cmd.exe", "/c rd /s /q \"" + norm + "\"", 30000);
                    }

                    if (Directory.Exists(norm))
                    {
                        errorMessage = "Algunos archivos dentro de la carpeta están actualmente abiertos o en uso por un proceso en ejecución. Cierra el programa e intenta de nuevo.";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static void DeleteDirectoryRecursiveFast(string dirPath, Action<string> onProgress)
        {
            ClearAttributes(dirPath);

            string[] files = null;
            try { files = Directory.GetFiles(dirPath); } catch { }
            if (files != null)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string f = files[i];
                    ClearAttributes(f);
                    File.Delete(f);
                }
            }

            string[] subDirs = null;
            try { subDirs = Directory.GetDirectories(dirPath); } catch { }
            if (subDirs != null)
            {
                for (int i = 0; i < subDirs.Length; i++)
                {
                    string sub = subDirs[i];
                    if (onProgress != null && (i % 15 == 0))
                    {
                        onProgress("Borrando: " + sub);
                    }
                    DeleteDirectoryRecursiveFast(sub, onProgress);
                }
            }

            ClearAttributes(dirPath);
            Directory.Delete(dirPath, false);
        }

        private static void ClearAttributes(string path)
        {
            try
            {
                string longPath = path.StartsWith("\\\\?\\") ? path : ("\\\\?\\" + path);
                SetFileAttributesW(longPath, FILE_ATTRIBUTE_NORMAL);
            }
            catch
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
        }

        private static void TakeOwnershipAndGrantAdmin(string path, bool recursive)
        {
            if (recursive)
            {
                RunHiddenCommand("takeown.exe", "/F \"" + path + "\" /R /A /D S", 25000);
                RunHiddenCommand("takeown.exe", "/F \"" + path + "\" /R /A /D Y", 25000);
                RunHiddenCommand("icacls.exe", "\"" + path + "\" /grant *S-1-5-32-544:F /T /C /Q", 25000);
            }
            else
            {
                RunHiddenCommand("takeown.exe", "/F \"" + path + "\" /A", 10000);
                RunHiddenCommand("icacls.exe", "\"" + path + "\" /grant *S-1-5-32-544:F /C /Q", 10000);
            }
        }

        private static void RunHiddenCommand(string fileName, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = args;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(timeoutMs);
                    }
                }
            }
            catch { }
        }

        public static void RemoveDeletedNodeFromMemory(
            FsNode deletedNode,
            InstallItem deletedInstall,
            string deletedPath,
            List<DiskItem> disks,
            List<InstallItem> allInstallations)
        {
            string normPath = FormatUtils.NormalizePath(deletedPath);

            // 1. If we have a specific FsNode, detach it from its Parent and subtract its size up the tree
            if (deletedNode != null)
            {
                DetachNodeFromParentChain(deletedNode);
            }
            else if (!string.IsNullOrEmpty(normPath) && disks != null)
            {
                // Find node by path in scanned disks
                for (int d = 0; d < disks.Count; d++)
                {
                    if (disks[d].RootNode != null)
                    {
                        FsNode found = FindNodeByPath(disks[d].RootNode, normPath);
                        if (found != null)
                        {
                            DetachNodeFromParentChain(found);
                            break;
                        }
                    }
                }
            }

            // 2. Remove matching items from TopFiles in all disks
            if (disks != null && !string.IsNullOrEmpty(normPath))
            {
                for (int d = 0; d < disks.Count; d++)
                {
                    DiskItem disk = disks[d];
                    if (disk.TopFiles != null)
                    {
                        disk.TopFiles.RemoveAll(delegate(FsNode f)
                        {
                            if (f == deletedNode) return true;
                            string fp = FormatUtils.NormalizePath(f.FullPath);
                            return string.Equals(fp, normPath, StringComparison.OrdinalIgnoreCase) ||
                                   fp.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase);
                        });
                    }

                    // Refresh disk used/free space from OS DriveInfo
                    try
                    {
                        DriveInfo di = new DriveInfo(disk.DriveLetter);
                        if (di.IsReady)
                        {
                            disk.TotalBytes = di.TotalSize;
                            disk.FreeBytes = di.TotalFreeSpace;
                            disk.UsedBytes = Math.Max(0L, disk.TotalBytes - disk.FreeBytes);
                            disk.UsedPercent = disk.TotalBytes > 0
                                ? ((double)disk.UsedBytes * 100.0 / (double)disk.TotalBytes)
                                : 0.0;
                        }
                    }
                    catch { }
                }
            }

            // 3. Remove matching installation if its folder was deleted
            if (allInstallations != null)
            {
                if (deletedInstall != null && (string.IsNullOrEmpty(deletedInstall.InstallLocation) || !Directory.Exists(deletedInstall.InstallLocation)))
                {
                    allInstallations.Remove(deletedInstall);
                }
                else if (!string.IsNullOrEmpty(normPath))
                {
                    allInstallations.RemoveAll(delegate(InstallItem inst)
                    {
                        string ip = FormatUtils.NormalizePath(inst.InstallLocation);
                        if (string.IsNullOrEmpty(ip)) return false;
                        return (string.Equals(ip, normPath, StringComparison.OrdinalIgnoreCase) ||
                                ip.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase)) &&
                               !Directory.Exists(ip);
                    });
                }
            }
        }

        private static void DetachNodeFromParentChain(FsNode node)
        {
            if (node == null || node.Parent == null) return;
            FsNode parent = node.Parent;
            if (parent.Children != null)
            {
                parent.Children.Remove(node);
            }

            long sizeRemoved = node.Size;
            int filesRemoved = node.FileCount;
            int foldersRemoved = node.IsFolder ? (1 + node.FolderCount) : 0;

            FsNode curr = parent;
            while (curr != null)
            {
                curr.Size = Math.Max(0L, curr.Size - sizeRemoved);
                curr.FileCount = Math.Max(0, curr.FileCount - filesRemoved);
                curr.FolderCount = Math.Max(0, curr.FolderCount - foldersRemoved);
                curr = curr.Parent;
            }
        }

        private static FsNode FindNodeByPath(FsNode root, string targetNormalizedPath)
        {
            if (root == null || string.IsNullOrEmpty(targetNormalizedPath)) return null;
            string rootNorm = FormatUtils.NormalizePath(root.FullPath);
            if (string.Equals(rootNorm, targetNormalizedPath, StringComparison.OrdinalIgnoreCase))
                return root;

            if (root.Children != null)
            {
                for (int i = 0; i < root.Children.Count; i++)
                {
                    FsNode child = root.Children[i];
                    string childNorm = FormatUtils.NormalizePath(child.FullPath);
                    if (string.Equals(childNorm, targetNormalizedPath, StringComparison.OrdinalIgnoreCase))
                        return child;
                    if (child.IsFolder && targetNormalizedPath.StartsWith(childNorm + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return FindNodeByPath(child, targetNormalizedPath);
                    }
                }
            }
            return null;
        }
    }
}
