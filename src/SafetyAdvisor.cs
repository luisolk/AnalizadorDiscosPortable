using System;
using System.Drawing;
using System.IO;

namespace AnalizadorDiscosPortable
{
    public enum SafetyLevel
    {
        SafeCacheOrTemp = 0,     // Muy seguro: Caché, temporales, logs, instaladores sobrantes
        SafeUserOrGame = 1,      // Seguro: Juegos, descargas, videos, documentos personales, portables
        CautionProgramData = 2,  // Precaución: Archivos de programas instalados, AppData, librerías
        CriticalSystem = 3       // Fundamental: Windows, System32, drivers, pagefile, redistribuibles críticos
    }

    public class SafetyAssessment
    {
        public SafetyLevel Level;
        public string VerdictBadge;       // e.g. "🟢 SEGURO DE BORRAR (No es del sistema)"
        public string WhatIsIt;           // e.g. "Juego instalado en tu librería de Steam."
        public string DeleteAdvice;       // e.g. "Puedes borrarlo sin ningún riesgo para Windows si ya no lo juegas."
        public Color BadgeColor;
    }

    public static class SafetyAdvisor
    {
        public static string FormatDateWithRelativeAge(DateTime dt)
        {
            if (dt <= DateTime.MinValue || dt.Year <= 1980)
                return "Fecha no disponible";

            TimeSpan age = DateTime.Now - dt;
            string rel;
            if (age.TotalSeconds < 0)
                rel = "reciente";
            else if (age.TotalHours < 1)
                rel = string.Format("hace {0} min", Math.Max(1, (int)age.TotalMinutes));
            else if (age.TotalDays < 1)
                rel = string.Format("hace {0} horas", Math.Max(1, (int)age.TotalHours));
            else if (age.TotalDays < 30)
                rel = string.Format("hace {0} días", (int)age.TotalDays);
            else if (age.TotalDays < 365)
            {
                int months = Math.Max(1, (int)(age.TotalDays / 30.4));
                rel = months == 1 ? "hace 1 mes" : string.Format("hace {0} meses", months);
            }
            else
            {
                int years = (int)(age.TotalDays / 365.25);
                int remMonths = (int)((age.TotalDays - (years * 365.25)) / 30.4);
                if (remMonths > 0)
                    rel = string.Format("hace {0} año(s) y {1} mes(es)", years, remMonths);
                else
                    rel = years == 1 ? "hace 1 año" : string.Format("hace {0} años", years);
            }

            return string.Format("{0:dd/MM/yyyy HH:mm} ({1})", dt, rel);
        }

        public static string FormatShortDate(DateTime dt)
        {
            if (dt <= DateTime.MinValue || dt.Year <= 1980)
                return "N/D";
            return dt.ToString("dd/MM/yyyy HH:mm");
        }

        public static SafetyAssessment AssessNode(FsNode node)
        {
            if (node == null)
            {
                return CreateDefaultAssessment();
            }

            if (node.MatchedInstall != null)
            {
                return AssessInstallation(node.MatchedInstall);
            }

            return AssessPathAndName(node.Name, node.FullPath, node.Extension, node.IsFolder, node.Category);
        }

        public static SafetyAssessment AssessInstallation(InstallItem inst)
        {
            if (inst == null) return CreateDefaultAssessment();

            string nameLower = (inst.Name ?? "").ToLowerInvariant();
            string pubLower = (inst.Publisher ?? "").ToLowerInvariant();
            string pathLower = (inst.InstallLocation ?? "").ToLowerInvariant();
            string sourceLower = (inst.SourceType ?? "").ToLowerInvariant();

            // 1. Check if it's a critical system runtime, driver, or Microsoft core component
            if (nameLower.Contains("visual c++") ||
                nameLower.Contains("redistributable") ||
                nameLower.Contains(".net runtime") ||
                nameLower.Contains(".net desktop") ||
                nameLower.Contains("microsoft.net") ||
                nameLower.Contains("windows sdk") ||
                nameLower.Contains("windows driver") ||
                nameLower.Contains("nvidia graphics") ||
                nameLower.Contains("nvidia physx") ||
                nameLower.Contains("amd software") ||
                nameLower.Contains("amd chipset") ||
                nameLower.Contains("intel(r) graphics") ||
                nameLower.Contains("realtek audio") ||
                nameLower.Contains("directx") ||
                nameLower.Contains("webview2"))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.CriticalSystem;
                sa.VerdictBadge = "🔴 FUNDAMENTAL / CONTROLADOR O LIBRERÍA DE SISTEMA";
                sa.WhatIsIt = "Componente base de Windows, controlador de hardware (GPU/Audio) o librería redistribuible (Visual C++ / .NET).";
                sa.DeleteAdvice = "NO SE RECOMIENDA BORRAR: Muchos juegos y programas dependen de este componente para poder arrancar.";
                sa.BadgeColor = Color.FromArgb(239, 68, 68);
                return sa;
            }

            // 2. Check if it's a video game (Steam, XboxGames, Epic, Riot, etc.)
            if (inst.Category == ItemCategory.Juegos ||
                sourceLower.Contains("juego") ||
                sourceLower.Contains("steam") ||
                pathLower.Contains("\\steamapps\\") ||
                pathLower.Contains("\\xboxgames\\") ||
                pathLower.Contains("\\epic games\\") ||
                pathLower.Contains("\\riot games\\"))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Videojuego / No afecta a Windows)";
                sa.WhatIsIt = string.Format("Videojuego instalado ({0}){1}.",
                    inst.SourceType,
                    string.IsNullOrEmpty(inst.Publisher) ? "" : (" • Editor: " + inst.Publisher));
                sa.DeleteAdvice = "No es fundamental para tu computadora. Si ya no lo juegas o está repetido, puedes borrarlo con total seguridad.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 3. Check if it's a portable folder on secondary drive D: or L:
            if (sourceLower.Contains("portable") || (!string.Equals(inst.DriveLetter, "C:", StringComparison.OrdinalIgnoreCase) && !pathLower.Contains("\\windows")))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Programa / Carpeta en Disco Secundario)";
                sa.WhatIsIt = string.Format("Aplicación o juego almacenado en el disco {0} ({1}).", inst.DriveLetter, inst.SourceType);
                sa.DeleteAdvice = "No es parte del sistema operativo Windows. Si ya no utilizas esta aplicación o tienes otra copia, puedes borrarla sin riesgo.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 4. Standard installed application on C:
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.CautionProgramData;
                sa.VerdictBadge = "🟡 PROGRAMA INSTALADO (Seguro borrar si ya no lo usas)";
                sa.WhatIsIt = string.Format("Aplicación instalada en el sistema ({0}){1}.",
                    inst.SourceType,
                    string.IsNullOrEmpty(inst.Publisher) ? "" : (" de " + inst.Publisher));
                sa.DeleteAdvice = "No es vital para que Windows funcione. Si ya no usas este programa, puedes desinstalarlo o borrar su carpeta para liberar espacio.";
                sa.BadgeColor = Color.FromArgb(245, 158, 11);
                return sa;
            }
        }

        public static SafetyAssessment AssessPathAndName(
            string name,
            string fullPath,
            string ext,
            bool isFolder,
            ItemCategory category)
        {
            string n = (name ?? "").ToLowerInvariant();
            string p = (fullPath ?? "").ToLowerInvariant();
            string e = (ext ?? "").ToLowerInvariant();

            // 1. Check critical Windows & OS files first
            if (n == "pagefile.sys" || n == "hiberfil.sys" || n == "swapfile.sys" ||
                n.Contains("espacio protegido del sistema") ||
                p == "c:\\windows" || p.StartsWith("c:\\windows\\system32") ||
                p.StartsWith("c:\\windows\\syswow64") || p.StartsWith("c:\\windows\\winsxs") ||
                p.StartsWith("c:\\windows\\boot") || p.StartsWith("c:\\windows\\inf") ||
                p.StartsWith("c:\\windows\\fonts") || p.StartsWith("c:\\windows\\servicing") ||
                p.Contains("\\system volume information") || p.StartsWith("c:\\recovery") ||
                p == "c:\\program files" || p == "c:\\program files (x86)" || p == "c:\\users")
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.CriticalSystem;
                sa.VerdictBadge = "🔴 FUNDAMENTAL PARA WINDOWS (NO BORRAR)";
                if (n == "pagefile.sys")
                    sa.WhatIsIt = "Archivo de Memoria Virtual (Paginación RAM) del sistema operativo Windows.";
                else if (n == "hiberfil.sys")
                    sa.WhatIsIt = "Archivo de Hibernación e Inicio Rápido del sistema operativo Windows.";
                else if (p.Contains("\\winsxs"))
                    sa.WhatIsIt = "Almacén de componentes internos y actualizaciones base de Windows (WinSxS).";
                else if (p.Contains("\\system32") || p.Contains("\\syswow64"))
                    sa.WhatIsIt = "Núcleo del sistema operativo Windows, controladores y librerías vitales.";
                else
                    sa.WhatIsIt = "Carpeta o archivo estructural del sistema operativo Windows.";

                sa.DeleteAdvice = "FUNDAMENTAL EN LA COMPUTADORA: No debes borrarlo manualmente porque dañaría el arranque o funcionamiento de Windows.";
                sa.BadgeColor = Color.FromArgb(239, 68, 68);
                return sa;
            }

            // 2. Check Temporary, Cache, CrashDump, Recycle Bin, Logs (Very Safe to Delete)
            if (p.Contains("\\temp\\") || p.EndsWith("\\temp") ||
                p.Contains("\\cache\\") || p.EndsWith("\\cache") ||
                p.Contains("\\gpucache") || p.Contains("\\shadercache") || p.Contains("\\d3dscache") ||
                p.Contains("\\crashdumps") || p.Contains("\\$recycle.bin") ||
                p.Contains("c:\\windows\\softwaredistribution\\download") ||
                p.Contains("c:\\windows.old") ||
                e == ".tmp" || e == ".dmp" || e == ".log" || e == ".bak" || e == ".old")
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeCacheOrTemp;
                sa.VerdictBadge = "🟢 MUY SEGURO DE BORRAR (Temporal / Caché / Respaldo)";
                sa.WhatIsIt = "Archivos temporales, memoria caché, registros (.log/.dmp) o papelera que los programas dejan acumulados.";
                sa.DeleteAdvice = "No es fundamental en absoluto. Puedes borrarlo con total tranquilidad para limpiar basura y recuperar espacio.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 3. Check User Downloads folder (Very common place for leftover installers, zips, videos)
            if (p.Contains("\\downloads\\") || p.EndsWith("\\downloads") || p.Contains("\\descargas\\"))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Carpeta de Descargas)";
                if (e == ".exe" || e == ".msi")
                    sa.WhatIsIt = "Instalador o programa descargado de Internet en tu carpeta de Descargas.";
                else if (e == ".zip" || e == ".rar" || e == ".7z" || e == ".iso")
                    sa.WhatIsIt = "Archivo comprimido o imagen ISO descargada de Internet.";
                else
                    sa.WhatIsIt = "Archivo personal guardado en tu carpeta de Descargas.";

                sa.DeleteAdvice = "Ningún archivo de la carpeta Descargas es necesario para que Windows funcione. Si ya lo instalaste o no lo ocupas, bórralo sin problema.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 4. Check Steam / Xbox / Epic / Riot / Games folders
            if (p.Contains("\\steamapps\\") || p.Contains("\\steamlibrary\\") ||
                p.Contains("\\xboxgames\\") || p.Contains("\\epic games\\") ||
                p.Contains("\\riot games\\") || category == ItemCategory.Juegos)
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Archivos de Videojuego)";
                sa.WhatIsIt = isFolder
                    ? "Carpeta de datos perteneciente a un videojuego instalado."
                    : "Archivo de recursos internos de un videojuego (mapas, texturas, audio o motor).";
                sa.DeleteAdvice = "No es fundamental para la computadora. Nota: si quieres borrar el juego, es mejor borrar la carpeta completa del juego y no solo un archivo suelto.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 5. Check Multimedia, Images, Compressed Archives, Personal Documents outside system dirs
            if (category == ItemCategory.VideoAudio || category == ItemCategory.Imagenes || category == ItemCategory.ComprimidosISO)
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Archivo Personal / Multimedia / Comprimido)";
                if (category == ItemCategory.VideoAudio)
                    sa.WhatIsIt = "Archivo multimedia de video o audio (" + (string.IsNullOrEmpty(e) ? "Carpeta" : e.ToUpperInvariant()) + ").";
                else if (category == ItemCategory.Imagenes)
                    sa.WhatIsIt = "Archivo de imagen, fotografía o diseño gráfico.";
                else
                    sa.WhatIsIt = "Paquete comprimido (.zip/.rar/.7z) o imagen de disco (.iso).";

                sa.DeleteAdvice = "No es un archivo del sistema operativo. Si ya no necesitas este archivo personal o ya lo descomprimiste, es 100% seguro borrarlo.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 6. Check AppData / ProgramData / Program Files
            if (p.Contains("\\appdata\\") || p.Contains("\\programdata\\") || p.Contains("\\program files"))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.CautionProgramData;
                sa.VerdictBadge = "🟠 PRECAUCIÓN (Datos o Archivos de un Programa)";
                if (p.Contains("\\appdata\\"))
                    sa.WhatIsIt = "Carpeta de datos de usuario, configuraciones, partidas guardadas o caché de una aplicación (AppData).";
                else if (p.Contains("\\programdata\\"))
                    sa.WhatIsIt = "Datos compartidos o paquetes de instalación de programas instalados (ProgramData).";
                else
                    sa.WhatIsIt = "Archivos internos de un programa instalado en Archivos de Programa.";

                sa.DeleteAdvice = "Si borras esta carpeta y el programa al que pertenece ya lo desinstalaste, es seguro. Si aún usas ese programa, podría perder su configuración o requerir reinstalación.";
                sa.BadgeColor = Color.FromArgb(245, 158, 11);
                return sa;
            }

            // 7. Check Windows folder other subdirectories
            if (p.StartsWith("c:\\windows\\"))
            {
                if (p.StartsWith("c:\\windows\\temp"))
                {
                    SafetyAssessment sa = new SafetyAssessment();
                    sa.Level = SafetyLevel.SafeCacheOrTemp;
                    sa.VerdictBadge = "🟢 MUY SEGURO DE BORRAR (Temporales de Windows)";
                    sa.WhatIsIt = "Carpeta de archivos temporales del sistema Windows.";
                    sa.DeleteAdvice = "Es seguro vaciar estos archivos temporales para recuperar espacio.";
                    sa.BadgeColor = Color.FromArgb(16, 185, 129);
                    return sa;
                }
                else
                {
                    SafetyAssessment sa = new SafetyAssessment();
                    sa.Level = SafetyLevel.CriticalSystem;
                    sa.VerdictBadge = "🔴 PRECAUCIÓN ALTA (Dentro de C:\\Windows)";
                    sa.WhatIsIt = "Archivo o carpeta ubicada dentro del directorio del sistema operativo Windows.";
                    sa.DeleteAdvice = "Evita borrar archivos dentro de C:\\Windows salvo que sean de C:\\Windows\\Temp.";
                    sa.BadgeColor = Color.FromArgb(239, 68, 68);
                    return sa;
                }
            }

            // 8. Standalone .exe / .msi installer on secondary drive (D:\, L:\) or Desktop
            if ((e == ".exe" || e == ".msi") && !p.Contains("\\program files"))
            {
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = SafetyLevel.SafeUserOrGame;
                sa.VerdictBadge = "🟢 SEGURO DE BORRAR (Ejecutable / Instalador Suelto)";
                sa.WhatIsIt = "Archivo ejecutable (.exe) o instalador (.msi) fuera de las carpetas del sistema.";
                sa.DeleteAdvice = "No es fundamental para Windows. Si es un instalador de un programa que ya instalaste, puedes borrarlo para ahorrar espacio.";
                sa.BadgeColor = Color.FromArgb(16, 185, 129);
                return sa;
            }

            // 9. General folder or file on C:, D:, L:
            {
                bool isSecondaryDrive = !p.StartsWith("c:\\");
                SafetyAssessment sa = new SafetyAssessment();
                sa.Level = isSecondaryDrive ? SafetyLevel.SafeUserOrGame : SafetyLevel.CautionProgramData;
                sa.VerdictBadge = isSecondaryDrive
                    ? "🟢 SEGURO PARA WINDOWS (Archivo/Carpeta en Disco Secundario)"
                    : "🟡 ARCHIVO O CARPETA DE USUARIO (Revisa antes de borrar)";
                sa.WhatIsIt = isFolder
                    ? "Carpeta de archivos almacenada en " + (fullPath ?? "el disco") + "."
                    : "Archivo (" + (string.IsNullOrEmpty(e) ? "sin extensión" : e) + ") almacenado en " + (fullPath ?? "el disco") + ".";
                sa.DeleteAdvice = isSecondaryDrive
                    ? "Está en un disco secundario (no contiene el sistema operativo Windows). Puedes borrarlo si ya no necesitas sus archivos."
                    : "No es un archivo crítico de arranque de Windows, pero verifica que no sean documentos personales que aún ocupes.";
                sa.BadgeColor = isSecondaryDrive ? Color.FromArgb(16, 185, 129) : Color.FromArgb(245, 158, 11);
                return sa;
            }
        }

        private static SafetyAssessment CreateDefaultAssessment()
        {
            SafetyAssessment sa = new SafetyAssessment();
            sa.Level = SafetyLevel.CautionProgramData;
            sa.VerdictBadge = "🟡 ELEMENTO EN DISCO";
            sa.WhatIsIt = "Archivo o carpeta almacenada en tu unidad.";
            sa.DeleteAdvice = "Verifica su ruta antes de eliminarlo.";
            sa.BadgeColor = Color.FromArgb(245, 158, 11);
            return sa;
        }
    }
}
