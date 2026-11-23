using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace AnalizadorDiscosPortable
{
    public enum ItemCategory
    {
        Instalacion,
        Juegos,
        VideoAudio,
        Imagenes,
        ComprimidosISO,
        DocumentosCodigo,
        SistemaWindows,
        CarpetaGeneral,
        OtrosArchivos
    }

    public class DiskItem
    {
        public string Name;          // e.g. "C:\"
        public string DriveLetter;   // e.g. "C:"
        public string VolumeLabel;   // e.g. "Windows"
        public string DriveFormat;   // e.g. "NTFS"
        public string DriveTypeDesc; // e.g. "Disco Local"
        public long TotalBytes;
        public long FreeBytes;
        public long UsedBytes;
        public double UsedPercent;
        public bool IsScanned;
        public bool IsScanning;
        public FsNode RootNode;
        public List<FsNode> TopFiles;
        public long ScannedFilesCount;
        public long ScannedFoldersCount;
        public long ScannedBytes;

        public DiskItem()
        {
            TopFiles = new List<FsNode>();
        }

        public string DisplayTitle
        {
            get
            {
                string lbl = string.IsNullOrWhiteSpace(VolumeLabel) ? DriveTypeDesc : VolumeLabel;
                return string.Format("{0} ({1})", DriveLetter, lbl);
            }
        }
    }

    public class FsNode
    {
        public string Name;
        public string FullPath;
        public string DriveLetter;
        public long Size;
        public long OwnFilesSize;
        public int FileCount;
        public int FolderCount;
        public bool IsFolder;
        public string Extension;
        public DateTime CreatedDate;
        public DateTime LastModified;
        public ItemCategory Category;
        public InstallItem MatchedInstall;
        public FsNode Parent;
        public List<FsNode> Children;

        public FsNode()
        {
            Children = new List<FsNode>();
            Category = ItemCategory.OtrosArchivos;
        }

        public void SortChildrenBySize()
        {
            if (Children != null && Children.Count > 1)
            {
                Children.Sort(delegate(FsNode a, FsNode b)
                {
                    return b.Size.CompareTo(a.Size);
                });
            }
        }
    }

    public class InstallItem
    {
        public string Name;
        public string Publisher;
        public string Version;
        public string InstallDate;
        public DateTime CreatedDate;
        public DateTime LastModified;
        public string InstallLocation;
        public string ExecutableOrIconPath;
        public string UninstallString;
        public string DriveLetter;
        public string SourceType; // "Registro Windows", "Juego / Launcher", "AppData / Usuario", "Program Files / Portable", "Microsoft Store"
        public long SizeBytes;
        public long RegistryEstimatedBytes;
        public bool SizeCalculatedFromDisk;
        public FsNode LinkedNode;
        public ItemCategory Category;

        public InstallItem()
        {
            Name = "";
            Publisher = "";
            Version = "";
            InstallDate = "";
            InstallLocation = "";
            ExecutableOrIconPath = "";
            UninstallString = "";
            DriveLetter = "";
            SourceType = "Registro Windows";
            Category = ItemCategory.Instalacion;
        }
    }

    public class TreemapRect
    {
        public RectangleF Bounds;
        public RectangleF HeaderBounds;
        public RectangleF ContentBounds;
        public string Title;
        public string Subtitle;
        public string Badge;
        public long SizeBytes;
        public double PercentOfParent;
        public double PercentOfDisk;
        public Color BaseColor;
        public bool IsGroup;
        public int Depth;
        public FsNode Node;
        public InstallItem Install;
        public List<TreemapRect> Children;

        public TreemapRect()
        {
            Children = new List<TreemapRect>();
            Title = "";
            Subtitle = "";
            Badge = "";
        }
    }

    public enum DuplicateMatchKind
    {
        RepeatedInstallation = 0,
        IdenticalFile = 1,
        VersionedOrSimilarFile = 2,
        SimilarFolder = 3
    }

    public class DuplicateEntry
    {
        public string Name;
        public string VersionOrVariant;
        public string FullPath;
        public string DriveLetter;
        public long SizeBytes;
        public string SourceDescription;
        public DateTime CreatedDate;
        public DateTime LastModified;
        public FsNode Node;
        public InstallItem Install;
        public DuplicateGroup ParentGroup;
    }

    public class DuplicateGroup
    {
        public string CanonicalBaseName;
        public string DisplayGroupTitle;
        public DuplicateMatchKind Kind;
        public long TotalSizeBytes;
        public long PotentialSavingsBytes;
        public List<DuplicateEntry> Entries;

        public DuplicateGroup()
        {
            Entries = new List<DuplicateEntry>();
        }

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case DuplicateMatchKind.RepeatedInstallation:
                        return "💿 Instalación Repetida / Versiones";
                    case DuplicateMatchKind.IdenticalFile:
                        return "📄 Archivo Idéntico Repetido";
                    case DuplicateMatchKind.VersionedOrSimilarFile:
                        return "🔄 Archivo Similar / Otra Versión";
                    case DuplicateMatchKind.SimilarFolder:
                        return "📁 Carpeta Repetida / Similar";
                    default:
                        return "Repetido / Similar";
                }
            }
        }
    }

    public static class FormatUtils
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1099511627776L) // 1 TB
                return string.Format("{0:0.00} TB", (double)bytes / 1099511627776.0);
            if (bytes >= 1073741824L) // 1 GB
                return string.Format("{0:0.00} GB", (double)bytes / 1073741824.0);
            if (bytes >= 1048576L) // 1 MB
                return string.Format("{0:0.0} MB", (double)bytes / 1048576.0);
            if (bytes >= 1024L) // 1 KB
                return string.Format("{0:0} KB", (double)bytes / 1024.0);
            return string.Format("{0} B", bytes);
        }

        public static string FormatNumber(long num)
        {
            return num.ToString("N0");
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                string p = path.Trim().Trim('"').Trim();
                p = p.Replace('/', '\\');
                while (p.EndsWith("\\") && p.Length > 3)
                {
                    p = p.Substring(0, p.Length - 1);
                }
                return p;
            }
            catch
            {
                return path;
            }
        }

        public static string GetDriveLetter(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 2) return "";
            if (path[1] == ':')
            {
                return path.Substring(0, 2).ToUpperInvariant();
            }
            return "";
        }

        public static ItemCategory ClassifyFile(string name, string ext, string fullPath)
        {
            if (string.IsNullOrEmpty(ext))
            {
                return ItemCategory.OtrosArchivos;
            }
            string e = ext.ToLowerInvariant();
            switch (e)
            {
                case ".exe":
                case ".msi":
                case ".dll":
                case ".sys":
                case ".ocx":
                case ".bat":
                case ".cmd":
                case ".ps1":
                    return ItemCategory.Instalacion;

                case ".pak":
                case ".vpk":
                case ".wad":
                case ".ucas":
                case ".utoc":
                case ".forge":
                case ".rpf":
                case ".unity3d":
                case ".assets":
                case ".ress":
                case ".ba2":
                case ".bsa":
                case ".gcf":
                case ".ncf":
                    return ItemCategory.Juegos;

                case ".mp4":
                case ".mkv":
                case ".avi":
                case ".mov":
                case ".wmv":
                case ".flv":
                case ".webm":
                case ".m4v":
                case ".mp3":
                case ".wav":
                case ".flac":
                case ".ogg":
                case ".aac":
                case ".m4a":
                    return ItemCategory.VideoAudio;

                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".gif":
                case ".bmp":
                case ".webp":
                case ".tiff":
                case ".psd":
                case ".ai":
                case ".svg":
                case ".ico":
                case ".raw":
                case ".cr2":
                case ".nef":
                    return ItemCategory.Imagenes;

                case ".zip":
                case ".rar":
                case ".7z":
                case ".tar":
                case ".gz":
                case ".bz2":
                case ".xz":
                case ".iso":
                case ".img":
                case ".vhd":
                case ".vhdx":
                case ".vmdk":
                case ".cab":
                case ".bak":
                    return ItemCategory.ComprimidosISO;

                case ".pdf":
                case ".doc":
                case ".docx":
                case ".xls":
                case ".xlsx":
                case ".ppt":
                case ".pptx":
                case ".txt":
                case ".csv":
                case ".json":
                case ".xml":
                case ".html":
                case ".css":
                case ".js":
                case ".ts":
                case ".py":
                case ".cs":
                case ".cpp":
                case ".c":
                case ".java":
                case ".sql":
                case ".db":
                case ".sqlite":
                case ".mdb":
                    return ItemCategory.DocumentosCodigo;

                case ".dmp":
                case ".log":
                case ".tmp":
                case ".etl":
                case ".evtx":
                    return ItemCategory.SistemaWindows;
            }

            if (!string.IsNullOrEmpty(name))
            {
                string lowerName = name.ToLowerInvariant();
                if (lowerName == "pagefile.sys" || lowerName == "hiberfil.sys" || lowerName == "swapfile.sys")
                {
                    return ItemCategory.SistemaWindows;
                }
            }

            return ItemCategory.OtrosArchivos;
        }

        public static ItemCategory ClassifyFolder(string name, string fullPath, InstallItem matchedInstall)
        {
            if (matchedInstall != null)
            {
                if (matchedInstall.SourceType != null && matchedInstall.SourceType.IndexOf("Juego", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ItemCategory.Juegos;
                return ItemCategory.Instalacion;
            }

            string p = (fullPath ?? "").ToLowerInvariant();
            string n = (name ?? "").ToLowerInvariant();

            if (p.Contains("\\steamapps\\") || p.Contains("\\steam\\") || p.Contains("\\epic games\\") ||
                p.Contains("\\xboxgames\\") || p.Contains("\\riot games\\") || p.Contains("\\ubisoft") ||
                p.Contains("\\ea games\\") || p.Contains("\\origin games\\") || p.Contains("\\gog galaxy\\") ||
                n == "games" || n == "juegos" || n == "steamlibrary" || n == "xboxgames")
            {
                return ItemCategory.Juegos;
            }

            if (p.Contains("\\windows\\") || p.EndsWith("\\windows") || n == "$recycle.bin" ||
                n == "system volume information" || n == "recovery" || n == "perflogs" || n == "windows.old")
            {
                return ItemCategory.SistemaWindows;
            }

            if (p.Contains("\\program files") || p.Contains("\\programdata") ||
                p.Contains("\\appdata\\local\\programs") || n == "programas" || n == "applications")
            {
                return ItemCategory.Instalacion;
            }

            if (n == "videos" || n == "music" || n == "música" || n == "peliculas" || n == "movies")
            {
                return ItemCategory.VideoAudio;
            }

            if (n == "pictures" || n == "imágenes" || n == "imagenes" || n == "photos" || n == "fotos")
            {
                return ItemCategory.Imagenes;
            }

            if (n == "documents" || n == "documentos" || n == "desktop" || n == "escritorio")
            {
                return ItemCategory.DocumentosCodigo;
            }

            if (n == "downloads" || n == "descargas")
            {
                return ItemCategory.ComprimidosISO;
            }

            return ItemCategory.CarpetaGeneral;
        }

        public static Color GetCategoryColor(ItemCategory cat)
        {
            switch (cat)
            {
                case ItemCategory.Instalacion:
                    return Color.FromArgb(59, 130, 246); // Blue - Instalaciones / Programas
                case ItemCategory.Juegos:
                    return Color.FromArgb(139, 92, 246); // Purple - Juegos / Launchers
                case ItemCategory.VideoAudio:
                    return Color.FromArgb(236, 72, 153); // Pink/Rose - Multimedia Video/Audio
                case ItemCategory.Imagenes:
                    return Color.FromArgb(20, 184, 166); // Teal - Imágenes / Diseño
                case ItemCategory.ComprimidosISO:
                    return Color.FromArgb(245, 158, 11); // Amber/Orange - Comprimidos / ISOs / Descargas
                case ItemCategory.DocumentosCodigo:
                    return Color.FromArgb(16, 185, 129); // Emerald Green - Documentos / Código
                case ItemCategory.SistemaWindows:
                    return Color.FromArgb(239, 68, 68);  // Red/Coral - Sistema / Windows
                case ItemCategory.CarpetaGeneral:
                    return Color.FromArgb(71, 105, 148); // Slate Blue - Carpetas Generales
                default:
                    return Color.FromArgb(100, 116, 139); // Neutral Slate - Otros Archivos
            }
        }

        public static string GetCategoryLabel(ItemCategory cat)
        {
            switch (cat)
            {
                case ItemCategory.Instalacion: return "Instalación / Programa";
                case ItemCategory.Juegos: return "Juego / Librería";
                case ItemCategory.VideoAudio: return "Video / Audio";
                case ItemCategory.Imagenes: return "Imagen / Diseño";
                case ItemCategory.ComprimidosISO: return "Comprimido / ISO / Imagen";
                case ItemCategory.DocumentosCodigo: return "Documento / Código / BD";
                case ItemCategory.SistemaWindows: return "Sistema / Windows";
                case ItemCategory.CarpetaGeneral: return "Carpeta";
                default: return "Otro Archivo";
            }
        }
    }
}
