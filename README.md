# Analizador de Discos Portable

Herramienta de escritorio moderna, ultraligera y portable para Windows que analiza el espacio en todos tus discos duros (`C:`, `D:`, `L:`, etc.), detecta instalaciones, encuentra archivos repetidos o versiones redundantes y muestra un mapa visual interactivo (*Treemap*) proporcional al peso de cada archivo y carpeta.

---

## Caracteristicas Principales

- **Treemap Visual Interactivo**: Cuadros proporcionales al peso real de los archivos. Permite hacer clic para explorar carpetas, retroceder y ver detalles al pasar el cursor.
- **Escaneo Ultrarrapido**: Basado en llamadas nativas de Windows Win32 (`FindFirstFileExW`) para procesar cientos de miles de archivos en segundos.
- **Deteccion Integral de Instalaciones**:
  - Registro de Windows (32 y 64 bits).
  - Paquetes de Microsoft Store / AppX.
  - Bibliotecas de juegos (Steam, Epic Games, Riot Games, Xbox Games).
- **Detector Inteligente de Duplicados y Versiones**:
  - Archivos identicos y copias repetidas entre diferentes unidades de disco.
  - Variaciones de version (ej. instaladores antiguos vs nuevos, carpetas clonadas).
- **Clasificacion Cronologica**: Pestana dedicada para ordenar archivos y programas desde el mas viejo al mas nuevo por disco o de manera global.
- **Asesor de Seguridad (SafetyAdvisor)**:
  - Clasifica cada elemento antes de eliminarlo (`Critico de Windows`, `Precaucion / Datos de Programa`, `Seguro / Usuario o Juegos`, `Temporal / Cache`).
  - Muestra la fecha exacta y la antiguedad relativa (ej. "hace 8 meses").
- **Acciones con Privilegios de Administrador**:
  - Enviar a la Papelera de Reciclaje de Windows.
  - Eliminacion forzada con toma automatica de posesion (`takeown` y permisos `icacls`) para carpetas rebeldes.
- **100% Portable**:
  - Un solo archivo `.exe` de ~183 KB.
  - No requiere instalacion ni componentes pesados.
  - Compatible con Windows 10 y Windows 11 (.NET Framework 4.8 integrado en el sistema).

---

## Compilacion

No requiere instalar Visual Studio. Se puede compilar directamente con el compilador que ya viene integrado en Windows:

```cmd
build.bat
```

O utilizando PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

---

## Estructura del Codigo

- `src/MainForm.cs`: Interfaz grafica principal, tarjetas de discos y sincronizacion de pestanas.
- `src/TreemapControl.cs`: Renderizado de cuadros proporcionales mediante GDI+ y tooltips informativos.
- `src/NativeScanner.cs`: Motor de indexacion rapida de directorios.
- `src/InstallScanner.cs`: Rastreador de programas, juegos y paquetes del sistema.
- `src/DuplicateScanner.cs`: Comparador de nombres, versiones y duplicados multi-disco.
- `src/SafetyAdvisor.cs`: Evaluador de riesgos y determinacion de impacto en el sistema operativo.
- `src/FileDeleter.cs`: Operaciones de borrado seguro y administracion de permisos NTFS.
