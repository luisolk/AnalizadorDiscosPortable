using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AnalizadorDiscosPortable
{
    public class MainForm : Form
    {
        private List<DiskItem> _disks = new List<DiskItem>();
        private List<InstallItem> _allInstallations = new List<InstallItem>();
        private List<DuplicateGroup> _allDuplicateGroups = new List<DuplicateGroup>();
        private DiskItem _selectedDisk = null; // null means "TODOS LOS DISCOS"
        private FsNode _currentFolderNode = null;
        private TreemapViewMode _treemapMode = TreemapViewMode.HierarchyTwoLevels;

        private volatile bool _cancelRequested = false;
        private bool _isScanningDisk = false;
        private bool _isScanningInstalls = false;
        private bool _isDeleting = false;

        // Track currently selected item across Treemap or active ListView
        private FsNode _lastSelectedNode = null;
        private InstallItem _lastSelectedInstall = null;
        private DuplicateEntry _lastSelectedDuplicate = null;

        // UI Controls
        private Panel _topHeaderPanel;
        private FlowLayoutPanel _disksFlowPanel;
        private Panel _toolbarPanel;
        private Button _btnUpLevel;
        private Button _btnRootLevel;
        private Label _lblBreadcrumb;
        private TextBox _txtSearch;
        private Button _btnClearSearch;

        private Button _btnModeHierarchy;
        private Button _btnModeFlat;
        private Button _btnModeInstalls;
        private Button _btnModeTopFiles;
        private Button _btnModeDuplicates;
        private Button _btnModeChronological;

        private Button _btnScanCurrent;
        private Button _btnScanAllDisks;
        private Button _btnExportReport;

        private SplitContainer _splitMain;
        private Panel _treemapContainerPanel;
        private Panel _legendPanel;
        private TreemapControl _treemap;

        private TabControl _tabLists;
        private TabPage _tabFiles;
        private TabPage _tabInstalls;
        private TabPage _tabTopFiles;
        private TabPage _tabDuplicates;
        private TabPage _tabChronological;

        private ListView _lvFiles;
        private ListView _lvInstalls;
        private ListView _lvTopFiles;
        private ListView _lvDuplicates;
        private ListView _lvChronological;

        private Panel _installFilterPanel;
        private ComboBox _cmbInstallDiskFilter;
        private ComboBox _cmbInstallTypeFilter;
        private Label _lblInstallSummary;
        private Button _btnInspectInstallFiles;
        private Button _btnOpenInstallFolder;
        private Button _btnDeleteInstall;

        private Panel _dupFilterPanel;
        private ComboBox _cmbDupDiskFilter;
        private ComboBox _cmbDupKindFilter;
        private Label _lblDupSummary;
        private Button _btnRefreshDuplicates;
        private Button _btnOpenDupFolder;
        private Button _btnDeleteDupItem;

        private Panel _chronoFilterPanel;
        private ComboBox _cmbChronoDiskFilter;
        private ComboBox _cmbChronoOrder;
        private ComboBox _cmbChronoScope;
        private ComboBox _cmbChronoMinSize;
        private Label _lblChronoSummary;
        private Button _btnOpenChronoFolder;
        private Button _btnDeleteChronoItem;

        private Panel _bottomStatusPanel;
        private ProgressBar _progressBar;
        private Label _lblStatusText;
        private Label _lblHoverInspector;
        private Button _btnCancelScan;
        private Button _btnOpenExplorerBottom;
        private Button _btnDeleteRecycleBottom;
        private Button _btnDeletePermanentBottom;

        private ContextMenuStrip _contextMenu;
        private object _contextTarget = null;

        // Current displayed lists for fast sorting
        private List<FsNode> _displayedFileNodes = new List<FsNode>();
        private List<InstallItem> _displayedInstallItems = new List<InstallItem>();
        private List<FsNode> _displayedTopFiles = new List<FsNode>();
        private List<DuplicateGroup> _displayedDuplicateGroups = new List<DuplicateGroup>();
        private List<DuplicateEntry> _displayedDuplicateEntries = new List<DuplicateEntry>();
        private List<FsNode> _displayedChronoNodes = new List<FsNode>();

        private int _filesSortCol = 1;
        private bool _filesSortAsc = false;
        private int _installsSortCol = 1;
        private bool _installsSortAsc = false;
        private int _topFilesSortCol = 1;
        private bool _topFilesSortAsc = false;
        private int _chronoSortCol = 1;
        private bool _chronoSortAsc = true; // Default: Oldest to Newest (Ascending Date)!

        private bool _suppressTabSync = false;

        public MainForm()
        {
            bool isAdmin = IsAdministrator();
            string adminTag = isAdmin ? "🛡️ [ADMINISTRADOR ACTIVO] " : "";
            this.Text = adminTag + "Analizador de Discos, Instalaciones y Repetidos Portable — Mapa de Cuadros y Borrado Directo";
            this.Size = new Size(1460, 890);
            this.MinimumSize = new Size(1024, 660);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(241, 245, 249);
            this.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            this.KeyPreview = true;
            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            InitializeComponentLayout();
            InitializeContextMenu();
        }

        public static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_splitMain != null && _splitMain.Width > 500)
            {
                try
                {
                    _splitMain.Panel1MinSize = 260;
                    _splitMain.Panel2MinSize = 260;
                    _splitMain.SplitterDistance = (int)(_splitMain.Width * 0.50);
                }
                catch { }
            }
            LoadDisksAndStartInitialDetection();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Delete && !_txtSearch.Focused)
            {
                e.Handled = true;
                bool permanent = e.Shift;
                DeleteCurrentlySelectedItem(permanent);
            }
        }

        private void InitializeComponentLayout()
        {
            // 1. Top Header with Disk Cards & Global Actions
            _topHeaderPanel = new Panel();
            _topHeaderPanel.Dock = DockStyle.Top;
            _topHeaderPanel.Height = 94;
            _topHeaderPanel.BackColor = Color.FromArgb(15, 23, 42);
            _topHeaderPanel.Padding = new Padding(8, 6, 8, 4);

            Panel actionsRightPanel = new Panel();
            actionsRightPanel.Dock = DockStyle.Right;
            actionsRightPanel.Width = 230;
            actionsRightPanel.Padding = new Padding(4, 2, 2, 2);

            _btnScanCurrent = CreateStyledButton("🔄 Escanear Disco Actual", Color.FromArgb(37, 99, 235), Color.White);
            _btnScanCurrent.Dock = DockStyle.Top;
            _btnScanCurrent.Height = 27;
            _btnScanCurrent.Click += delegate { StartScanForSelectedDisk(true); };

            _btnScanAllDisks = CreateStyledButton("⚡ Escanear TODOS los Discos", Color.FromArgb(16, 185, 129), Color.White);
            _btnScanAllDisks.Dock = DockStyle.Top;
            _btnScanAllDisks.Height = 27;
            _btnScanAllDisks.Click += delegate { StartScanAllDisks(); };

            _btnExportReport = CreateStyledButton("📊 Exportar Reporte HTML/CSV", Color.FromArgb(51, 65, 85), Color.FromArgb(226, 232, 240));
            _btnExportReport.Dock = DockStyle.Bottom;
            _btnExportReport.Height = 25;
            _btnExportReport.Click += delegate { ExportInteractiveReport(); };

            actionsRightPanel.Controls.Add(_btnExportReport);
            actionsRightPanel.Controls.Add(_btnScanAllDisks);
            actionsRightPanel.Controls.Add(_btnScanCurrent);

            _disksFlowPanel = new FlowLayoutPanel();
            _disksFlowPanel.Dock = DockStyle.Fill;
            _disksFlowPanel.AutoScroll = true;
            _disksFlowPanel.WrapContents = false;
            _disksFlowPanel.BackColor = Color.FromArgb(15, 23, 42);

            _topHeaderPanel.Controls.Add(_disksFlowPanel);
            _topHeaderPanel.Controls.Add(actionsRightPanel);

            // 2. Navigation, Search & Treemap Mode Bar
            _toolbarPanel = new Panel();
            _toolbarPanel.Dock = DockStyle.Top;
            _toolbarPanel.Height = 76;
            _toolbarPanel.BackColor = Color.FromArgb(30, 41, 59);
            _toolbarPanel.Padding = new Padding(10, 5, 10, 5);

            // Top row inside toolbar: Breadcrumb navigation + Search box
            Panel navRow = new Panel();
            navRow.Dock = DockStyle.Top;
            navRow.Height = 32;

            _btnRootLevel = CreateStyledButton("🏠 Raíz", Color.FromArgb(51, 65, 85), Color.White);
            _btnRootLevel.Width = 72;
            _btnRootLevel.Dock = DockStyle.Left;
            _btnRootLevel.Click += delegate { NavigateToDiskRoot(); };

            Panel sep1 = new Panel { Width = 6, Dock = DockStyle.Left };

            _btnUpLevel = CreateStyledButton("⬅ Subir Nivel", Color.FromArgb(51, 65, 85), Color.White);
            _btnUpLevel.Width = 104;
            _btnUpLevel.Dock = DockStyle.Left;
            _btnUpLevel.Click += delegate { NavigateUpOneLevel(); };

            Panel sepAdmin = new Panel { Width = 6, Dock = DockStyle.Left };
            bool isAdminNow = IsAdministrator();
            Button btnAdminBadge = CreateStyledButton(
                isAdminNow ? "🛡️ ADMIN ACTIVO" : "🛡️ Elevar a Admin",
                isAdminNow ? Color.FromArgb(21, 128, 61) : Color.FromArgb(180, 83, 9),
                Color.White);
            btnAdminBadge.Width = 132;
            btnAdminBadge.Dock = DockStyle.Left;
            btnAdminBadge.Click += delegate
            {
                if (IsAdministrator())
                {
                    MessageBox.Show("La aplicación ya se está ejecutando con permisos completos de Administrador.\nPuedes borrar cualquier archivo, carpeta o instalación protegida.",
                        "Modo Administrador Activo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo();
                        psi.FileName = Application.ExecutablePath;
                        psi.Arguments = "--elevated";
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";
                        Process.Start(psi);
                        Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("No se pudo elevar a Administrador desde este entorno:\n" + ex.Message +
                            "\n\nTip: Haz clic derecho sobre AnalizadorDiscosPortable.exe en el Explorador de Windows y elige 'Ejecutar como administrador'.",
                            "Permisos de Administrador", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };

            Panel searchPanel = new Panel();
            searchPanel.Dock = DockStyle.Right;
            searchPanel.Width = 310;

            _btnClearSearch = CreateStyledButton("✕", Color.FromArgb(71, 85, 105), Color.White);
            _btnClearSearch.Dock = DockStyle.Right;
            _btnClearSearch.Width = 28;
            _btnClearSearch.Click += delegate
            {
                _txtSearch.Text = "";
            };

            _txtSearch = new TextBox();
            _txtSearch.Dock = DockStyle.Fill;
            _txtSearch.BackColor = Color.FromArgb(15, 23, 42);
            _txtSearch.ForeColor = Color.White;
            _txtSearch.BorderStyle = BorderStyle.FixedSingle;
            _txtSearch.Font = new Font("Segoe UI", 9.5f);
            _txtSearch.TextChanged += delegate { RefreshCurrentViews(false); };

            Label lblSearchIcon = new Label();
            lblSearchIcon.Text = "🔍 Buscar:";
            lblSearchIcon.AutoSize = false;
            lblSearchIcon.Width = 72;
            lblSearchIcon.Dock = DockStyle.Left;
            lblSearchIcon.TextAlign = ContentAlignment.MiddleRight;
            lblSearchIcon.ForeColor = Color.FromArgb(148, 163, 184);

            searchPanel.Controls.Add(_txtSearch);
            searchPanel.Controls.Add(_btnClearSearch);
            searchPanel.Controls.Add(lblSearchIcon);

            _lblBreadcrumb = new Label();
            _lblBreadcrumb.Dock = DockStyle.Fill;
            _lblBreadcrumb.TextAlign = ContentAlignment.MiddleLeft;
            _lblBreadcrumb.Padding = new Padding(10, 0, 10, 0);
            _lblBreadcrumb.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _lblBreadcrumb.ForeColor = Color.FromArgb(250, 204, 21);
            _lblBreadcrumb.AutoEllipsis = true;
            _lblBreadcrumb.Text = "📍 Selecciona un disco...";

            navRow.Controls.Add(_lblBreadcrumb);
            navRow.Controls.Add(searchPanel);
            navRow.Controls.Add(btnAdminBadge);
            navRow.Controls.Add(sepAdmin);
            navRow.Controls.Add(_btnUpLevel);
            navRow.Controls.Add(sep1);
            navRow.Controls.Add(_btnRootLevel);

            // Second row inside toolbar: Treemap Mode Buttons
            Panel modeRow = new Panel();
            modeRow.Dock = DockStyle.Bottom;
            modeRow.Height = 30;

            Label lblModeTitle = new Label();
            lblModeTitle.Text = "Mapa de Cuadros:";
            lblModeTitle.Dock = DockStyle.Left;
            lblModeTitle.Width = 114;
            lblModeTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblModeTitle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            lblModeTitle.ForeColor = Color.FromArgb(148, 163, 184);

            _btnModeHierarchy = CreateStyledButton("📦 Carpetas y Archivos (2 Niveles)", Color.FromArgb(37, 99, 235), Color.White);
            _btnModeHierarchy.Dock = DockStyle.Left;
            _btnModeHierarchy.Width = 195;
            _btnModeHierarchy.Click += delegate { SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true); };

            Panel mSep1 = new Panel { Width = 4, Dock = DockStyle.Left };

            _btnModeFlat = CreateStyledButton("📁 Nivel Actual (1 Nivel)", Color.FromArgb(51, 65, 85), Color.White);
            _btnModeFlat.Dock = DockStyle.Left;
            _btnModeFlat.Width = 148;
            _btnModeFlat.Click += delegate { SetTreemapMode(TreemapViewMode.CurrentLevelFlat, true); };

            Panel mSep2 = new Panel { Width = 4, Dock = DockStyle.Left };

            _btnModeInstalls = CreateStyledButton("💿 TODO LO INSTALADO", Color.FromArgb(51, 65, 85), Color.White);
            _btnModeInstalls.Dock = DockStyle.Left;
            _btnModeInstalls.Width = 166;
            _btnModeInstalls.Click += delegate { SetTreemapMode(TreemapViewMode.InstallationsMap, true); };

            Panel mSep3 = new Panel { Width = 4, Dock = DockStyle.Left };

            _btnModeDuplicates = CreateStyledButton("🧬 REPETIDOS Y VERSIONES", Color.FromArgb(51, 65, 85), Color.White);
            _btnModeDuplicates.Dock = DockStyle.Left;
            _btnModeDuplicates.Width = 185;
            _btnModeDuplicates.Click += delegate { SetTreemapMode(TreemapViewMode.DuplicatesAndSimilarsMap, true); };

            Panel mSep4 = new Panel { Width = 4, Dock = DockStyle.Left };

            _btnModeChronological = CreateStyledButton("⏳ MÁS VIEJO A NUEVO", Color.FromArgb(51, 65, 85), Color.White);
            _btnModeChronological.Dock = DockStyle.Left;
            _btnModeChronological.Width = 165;
            _btnModeChronological.Click += delegate { SetTreemapMode(TreemapViewMode.ChronologicalAgeMap, true); };

            Panel mSep5 = new Panel { Width = 4, Dock = DockStyle.Left };

            _btnModeTopFiles = CreateStyledButton("🔥 Top Archivos Pesados", Color.FromArgb(51, 65, 85), Color.White);
            _btnModeTopFiles.Dock = DockStyle.Left;
            _btnModeTopFiles.Width = 168;
            _btnModeTopFiles.Click += delegate { SetTreemapMode(TreemapViewMode.TopHeavyFilesMap, true); };

            modeRow.Controls.Add(_btnModeTopFiles);
            modeRow.Controls.Add(mSep5);
            modeRow.Controls.Add(_btnModeChronological);
            modeRow.Controls.Add(mSep4);
            modeRow.Controls.Add(_btnModeDuplicates);
            modeRow.Controls.Add(mSep3);
            modeRow.Controls.Add(_btnModeInstalls);
            modeRow.Controls.Add(mSep2);
            modeRow.Controls.Add(_btnModeFlat);
            modeRow.Controls.Add(mSep1);
            modeRow.Controls.Add(_btnModeHierarchy);
            modeRow.Controls.Add(lblModeTitle);

            _toolbarPanel.Controls.Add(navRow);
            _toolbarPanel.Controls.Add(modeRow);

            // 3. Bottom Status & Inspector Bar with Delete & Explorer Action Buttons
            _bottomStatusPanel = new Panel();
            _bottomStatusPanel.Dock = DockStyle.Bottom;
            _bottomStatusPanel.Height = 56;
            _bottomStatusPanel.BackColor = Color.FromArgb(15, 23, 42);
            _bottomStatusPanel.Padding = new Padding(10, 4, 10, 4);

            Panel statusTopRow = new Panel { Dock = DockStyle.Top, Height = 26 };
            _progressBar = new ProgressBar();
            _progressBar.Dock = DockStyle.Left;
            _progressBar.Width = 180;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Visible = false;

            _btnCancelScan = CreateStyledButton("⏹ Detener Escaneo", Color.FromArgb(220, 38, 38), Color.White);
            _btnCancelScan.Dock = DockStyle.Left;
            _btnCancelScan.Width = 135;
            _btnCancelScan.Visible = false;
            _btnCancelScan.Click += delegate { _cancelRequested = true; };

            _btnDeletePermanentBottom = CreateStyledButton("🔥 Borrar Permanente (Admin)", Color.FromArgb(185, 28, 28), Color.White);
            _btnDeletePermanentBottom.Dock = DockStyle.Right;
            _btnDeletePermanentBottom.Width = 195;
            _btnDeletePermanentBottom.Click += delegate { DeleteCurrentlySelectedItem(true); };

            Panel bSep1 = new Panel { Width = 6, Dock = DockStyle.Right };

            _btnDeleteRecycleBottom = CreateStyledButton("🗑️ Enviar a Papelera", Color.FromArgb(217, 119, 6), Color.White);
            _btnDeleteRecycleBottom.Dock = DockStyle.Right;
            _btnDeleteRecycleBottom.Width = 150;
            _btnDeleteRecycleBottom.Click += delegate { DeleteCurrentlySelectedItem(false); };

            Panel bSep2 = new Panel { Width = 6, Dock = DockStyle.Right };

            _btnOpenExplorerBottom = CreateStyledButton("📂 Abrir en Explorador", Color.FromArgb(51, 65, 85), Color.White);
            _btnOpenExplorerBottom.Dock = DockStyle.Right;
            _btnOpenExplorerBottom.Width = 158;
            _btnOpenExplorerBottom.Click += delegate { OpenCurrentSelectedInExplorer(); };

            _lblStatusText = new Label();
            _lblStatusText.Dock = DockStyle.Fill;
            _lblStatusText.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatusText.Padding = new Padding(8, 0, 8, 0);
            _lblStatusText.ForeColor = Color.FromArgb(125, 211, 252);
            _lblStatusText.Font = new Font("Segoe UI", 8.75f, FontStyle.Bold);
            _lblStatusText.AutoEllipsis = true;
            _lblStatusText.Text = "Iniciando detector de discos, instalaciones y repetidos...";

            statusTopRow.Controls.Add(_lblStatusText);
            statusTopRow.Controls.Add(_btnOpenExplorerBottom);
            statusTopRow.Controls.Add(bSep2);
            statusTopRow.Controls.Add(_btnDeleteRecycleBottom);
            statusTopRow.Controls.Add(bSep1);
            statusTopRow.Controls.Add(_btnDeletePermanentBottom);
            statusTopRow.Controls.Add(_btnCancelScan);
            statusTopRow.Controls.Add(_progressBar);

            _lblHoverInspector = new Label();
            _lblHoverInspector.Dock = DockStyle.Bottom;
            _lblHoverInspector.Height = 20;
            _lblHoverInspector.TextAlign = ContentAlignment.MiddleLeft;
            _lblHoverInspector.ForeColor = Color.FromArgb(203, 213, 225);
            _lblHoverInspector.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _lblHoverInspector.AutoEllipsis = true;
            _lblHoverInspector.Text = "💡 Selecciona cualquier cuadro o fila y usa los botones '🗑️ Enviar a Papelera' o '🔥 Borrar Permanente (Admin)' (o clic derecho) para liberar espacio.";

            _bottomStatusPanel.Controls.Add(statusTopRow);
            _bottomStatusPanel.Controls.Add(_lblHoverInspector);

            // 4. Main Split Workspace (Left: Interactive Treemap + Legend; Right: Synchronized Lists)
            _splitMain = new SplitContainer();
            _splitMain.Dock = DockStyle.Fill;
            _splitMain.Orientation = Orientation.Vertical;
            _splitMain.SplitterWidth = 6;
            _splitMain.BackColor = Color.FromArgb(51, 65, 85);

            // Left Panel: Treemap + Color Legend
            _treemapContainerPanel = new Panel();
            _treemapContainerPanel.Dock = DockStyle.Fill;
            _treemapContainerPanel.BackColor = Color.FromArgb(15, 23, 42);

            _legendPanel = CreateColorLegendPanel();
            _legendPanel.Dock = DockStyle.Bottom;
            _legendPanel.Height = 28;

            _treemap = new TreemapControl();
            _treemap.Dock = DockStyle.Fill;
            _treemap.ItemClicked += OnTreemapItemClicked;
            _treemap.ItemDoubleClicked += OnTreemapItemDoubleClicked;
            _treemap.ItemRightClicked += OnTreemapItemRightClicked;
            _treemap.ItemHovered += OnTreemapItemHovered;

            _treemapContainerPanel.Controls.Add(_treemap);
            _treemapContainerPanel.Controls.Add(_legendPanel);
            _splitMain.Panel1.Controls.Add(_treemapContainerPanel);

            // Right Panel: TabControl with 4 Lists
            _tabLists = new TabControl();
            _tabLists.Dock = DockStyle.Fill;
            _tabLists.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            _tabLists.SelectedIndexChanged += OnTabListsSelectedIndexChanged;

            // Tab 1: Carpetas y Archivos
            _tabFiles = new TabPage("📁 Archivos y Carpetas");
            _tabFiles.BackColor = Color.FromArgb(15, 23, 42);

            _lvFiles = CreateStyledListView();
            _lvFiles.Columns.Add("Nombre de Archivo / Carpeta", 215);
            _lvFiles.Columns.Add("Tamaño", 88, HorizontalAlignment.Right);
            _lvFiles.Columns.Add("% Disco", 65, HorizontalAlignment.Right);
            _lvFiles.Columns.Add("📅 Fecha Agregado", 125);
            _lvFiles.Columns.Add("🛡️ ¿Es Fundamental? / Seguridad", 215);
            _lvFiles.Columns.Add("Tipo / Instalación", 155);
            _lvFiles.Columns.Add("Archivos", 72, HorizontalAlignment.Right);
            _lvFiles.Columns.Add("Ruta Completa", 260);
            _lvFiles.SelectedIndexChanged += OnLvFilesSelectedIndexChanged;
            _lvFiles.DoubleClick += OnLvFilesDoubleClick;
            _lvFiles.ColumnClick += OnLvFilesColumnClick;
            _lvFiles.MouseUp += OnLvFilesMouseUp;
            _tabFiles.Controls.Add(_lvFiles);

            // Tab 2: TODO LO INSTALADO (Instalaciones)
            _tabInstalls = new TabPage("💿 TODO LO INSTALADO");
            _tabInstalls.BackColor = Color.FromArgb(15, 23, 42);

            _installFilterPanel = new Panel();
            _installFilterPanel.Dock = DockStyle.Top;
            _installFilterPanel.Height = 64;
            _installFilterPanel.BackColor = Color.FromArgb(30, 41, 59);
            _installFilterPanel.Padding = new Padding(8, 5, 8, 5);

            Panel instRow1 = new Panel { Dock = DockStyle.Top, Height = 26 };
            Label lblFilterDisk = new Label
            {
                Text = "Disco:",
                Dock = DockStyle.Left,
                Width = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _cmbInstallDiskFilter = new ComboBox();
            _cmbInstallDiskFilter.Dock = DockStyle.Left;
            _cmbInstallDiskFilter.Width = 120;
            _cmbInstallDiskFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInstallDiskFilter.BackColor = Color.FromArgb(15, 23, 42);
            _cmbInstallDiskFilter.ForeColor = Color.White;
            _cmbInstallDiskFilter.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _cmbInstallDiskFilter.Items.Add("Todos los Discos");
            _cmbInstallDiskFilter.SelectedIndex = 0;
            _cmbInstallDiskFilter.SelectedIndexChanged += delegate
            {
                PopulateInstallationsList();
                if (_treemapMode == TreemapViewMode.InstallationsMap)
                    UpdateTreemapView();
            };

            Label lblFilterType = new Label
            {
                Text = " Origen:",
                Dock = DockStyle.Left,
                Width = 52,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _cmbInstallTypeFilter = new ComboBox();
            _cmbInstallTypeFilter.Dock = DockStyle.Left;
            _cmbInstallTypeFilter.Width = 150;
            _cmbInstallTypeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInstallTypeFilter.BackColor = Color.FromArgb(15, 23, 42);
            _cmbInstallTypeFilter.ForeColor = Color.White;
            _cmbInstallTypeFilter.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _cmbInstallTypeFilter.Items.AddRange(new object[]
            {
                "Todos los Tipos",
                "Registro de Windows",
                "Juegos / Librerías",
                "Program Files / Portables",
                "AppData / Usuario",
                "Microsoft Store / AppX"
            });
            _cmbInstallTypeFilter.SelectedIndex = 0;
            _cmbInstallTypeFilter.SelectedIndexChanged += delegate
            {
                PopulateInstallationsList();
                if (_treemapMode == TreemapViewMode.InstallationsMap)
                    UpdateTreemapView();
            };

            _btnDeleteInstall = CreateStyledButton("🔥 Borrar / Desinstalar", Color.FromArgb(185, 28, 28), Color.White);
            _btnDeleteInstall.Dock = DockStyle.Right;
            _btnDeleteInstall.Width = 148;
            _btnDeleteInstall.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnDeleteInstall.Click += delegate { PromptDeleteOrUninstallSelectedInstall(); };

            Panel iSep0 = new Panel { Width = 5, Dock = DockStyle.Right };

            _btnOpenInstallFolder = CreateStyledButton("📂 Carpeta", Color.FromArgb(51, 65, 85), Color.White);
            _btnOpenInstallFolder.Dock = DockStyle.Right;
            _btnOpenInstallFolder.Width = 82;
            _btnOpenInstallFolder.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnOpenInstallFolder.Click += delegate { OpenSelectedInstallInExplorer(); };

            Panel iSep = new Panel { Width = 5, Dock = DockStyle.Right };

            _btnInspectInstallFiles = CreateStyledButton("🔍 Ver en Cuadros", Color.FromArgb(37, 99, 235), Color.White);
            _btnInspectInstallFiles.Dock = DockStyle.Right;
            _btnInspectInstallFiles.Width = 122;
            _btnInspectInstallFiles.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnInspectInstallFiles.Click += delegate { DrillIntoSelectedInstallation(); };

            instRow1.Controls.Add(_btnInspectInstallFiles);
            instRow1.Controls.Add(iSep);
            instRow1.Controls.Add(_btnOpenInstallFolder);
            instRow1.Controls.Add(iSep0);
            instRow1.Controls.Add(_btnDeleteInstall);
            instRow1.Controls.Add(_cmbInstallTypeFilter);
            instRow1.Controls.Add(lblFilterType);
            instRow1.Controls.Add(_cmbInstallDiskFilter);
            instRow1.Controls.Add(lblFilterDisk);

            _lblInstallSummary = new Label();
            _lblInstallSummary.Dock = DockStyle.Bottom;
            _lblInstallSummary.Height = 24;
            _lblInstallSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblInstallSummary.ForeColor = Color.FromArgb(250, 204, 21);
            _lblInstallSummary.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblInstallSummary.Text = "Detectando todo lo instalado en todos los discos...";

            _installFilterPanel.Controls.Add(instRow1);
            _installFilterPanel.Controls.Add(_lblInstallSummary);

            _lvInstalls = CreateStyledListView();
            _lvInstalls.Columns.Add("Programa / Juego Instalado", 205);
            _lvInstalls.Columns.Add("Tamaño Real", 90, HorizontalAlignment.Right);
            _lvInstalls.Columns.Add("Disco", 50, HorizontalAlignment.Center);
            _lvInstalls.Columns.Add("📅 Fecha Agregado", 125);
            _lvInstalls.Columns.Add("🛡️ ¿Es Fundamental? / Seguridad", 215);
            _lvInstalls.Columns.Add("Tipo / Origen", 150);
            _lvInstalls.Columns.Add("Editor / Fabricante", 135);
            _lvInstalls.Columns.Add("Ruta de Instalación", 260);
            _lvInstalls.SelectedIndexChanged += OnLvInstallsSelectedIndexChanged;
            _lvInstalls.DoubleClick += delegate { DrillIntoSelectedInstallation(); };
            _lvInstalls.ColumnClick += OnLvInstallsColumnClick;
            _lvInstalls.MouseUp += OnLvInstallsMouseUp;

            _tabInstalls.Controls.Add(_lvInstalls);
            _tabInstalls.Controls.Add(_installFilterPanel);

            // Tab 3: REPETIDOS, VERSIONES DIFERENTES Y SIMILARES
            _tabDuplicates = new TabPage("🧬 Repetidos y Similares");
            _tabDuplicates.BackColor = Color.FromArgb(15, 23, 42);

            _dupFilterPanel = new Panel();
            _dupFilterPanel.Dock = DockStyle.Top;
            _dupFilterPanel.Height = 64;
            _dupFilterPanel.BackColor = Color.FromArgb(30, 41, 59);
            _dupFilterPanel.Padding = new Padding(8, 5, 8, 5);

            Panel dupRow1 = new Panel { Dock = DockStyle.Top, Height = 26 };
            Label lblDupDisk = new Label
            {
                Text = "Disco:",
                Dock = DockStyle.Left,
                Width = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _cmbDupDiskFilter = new ComboBox();
            _cmbDupDiskFilter.Dock = DockStyle.Left;
            _cmbDupDiskFilter.Width = 115;
            _cmbDupDiskFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDupDiskFilter.BackColor = Color.FromArgb(15, 23, 42);
            _cmbDupDiskFilter.ForeColor = Color.White;
            _cmbDupDiskFilter.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _cmbDupDiskFilter.Items.Add("Todos los Discos");
            _cmbDupDiskFilter.SelectedIndex = 0;
            _cmbDupDiskFilter.SelectedIndexChanged += delegate
            {
                PopulateDuplicatesList(false);
                if (_treemapMode == TreemapViewMode.DuplicatesAndSimilarsMap)
                    UpdateTreemapView();
            };

            Label lblDupKind = new Label
            {
                Text = " Mostrar:",
                Dock = DockStyle.Left,
                Width = 58,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _cmbDupKindFilter = new ComboBox();
            _cmbDupKindFilter.Dock = DockStyle.Left;
            _cmbDupKindFilter.Width = 220;
            _cmbDupKindFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDupKindFilter.BackColor = Color.FromArgb(15, 23, 42);
            _cmbDupKindFilter.ForeColor = Color.White;
            _cmbDupKindFilter.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _cmbDupKindFilter.Items.AddRange(new object[]
            {
                "Todos (Instalaciones + Archivos + Versiones)",
                "💿 Solo Instalaciones Repetidas / Versiones",
                "📄 Archivos Idénticos Repetidos (Mismo Peso)",
                "🔄 Archivos de Distinta Versión / Mismo Nombre",
                "📁 Carpetas Repetidas o Similares"
            });
            _cmbDupKindFilter.SelectedIndex = 0;
            _cmbDupKindFilter.SelectedIndexChanged += delegate
            {
                PopulateDuplicatesList(false);
                if (_treemapMode == TreemapViewMode.DuplicatesAndSimilarsMap)
                    UpdateTreemapView();
            };

            _btnDeleteDupItem = CreateStyledButton("🔥 Borrar Copia Seleccionada", Color.FromArgb(185, 28, 28), Color.White);
            _btnDeleteDupItem.Dock = DockStyle.Right;
            _btnDeleteDupItem.Width = 172;
            _btnDeleteDupItem.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnDeleteDupItem.Click += delegate { DeleteCurrentlySelectedItem(true); };

            Panel dSep0 = new Panel { Width = 5, Dock = DockStyle.Right };

            _btnOpenDupFolder = CreateStyledButton("📂 Ubicación", Color.FromArgb(51, 65, 85), Color.White);
            _btnOpenDupFolder.Dock = DockStyle.Right;
            _btnOpenDupFolder.Width = 90;
            _btnOpenDupFolder.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnOpenDupFolder.Click += delegate { OpenCurrentSelectedInExplorer(); };

            Panel dSep1 = new Panel { Width = 5, Dock = DockStyle.Right };

            _btnRefreshDuplicates = CreateStyledButton("🔄 Re-analizar", Color.FromArgb(37, 99, 235), Color.White);
            _btnRefreshDuplicates.Dock = DockStyle.Right;
            _btnRefreshDuplicates.Width = 98;
            _btnRefreshDuplicates.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnRefreshDuplicates.Click += delegate { PopulateDuplicatesList(true); UpdateTreemapView(); };

            dupRow1.Controls.Add(_btnRefreshDuplicates);
            dupRow1.Controls.Add(dSep1);
            dupRow1.Controls.Add(_btnOpenDupFolder);
            dupRow1.Controls.Add(dSep0);
            dupRow1.Controls.Add(_btnDeleteDupItem);
            dupRow1.Controls.Add(_cmbDupKindFilter);
            dupRow1.Controls.Add(lblDupKind);
            dupRow1.Controls.Add(_cmbDupDiskFilter);
            dupRow1.Controls.Add(lblDupDisk);

            _lblDupSummary = new Label();
            _lblDupSummary.Dock = DockStyle.Bottom;
            _lblDupSummary.Height = 24;
            _lblDupSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblDupSummary.ForeColor = Color.FromArgb(250, 204, 21);
            _lblDupSummary.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblDupSummary.Text = "Analizando instalaciones repetidas, versiones diferentes y archivos duplicados...";

            _dupFilterPanel.Controls.Add(dupRow1);
            _dupFilterPanel.Controls.Add(_lblDupSummary);

            _lvDuplicates = CreateStyledListView();
            _lvDuplicates.Columns.Add("Grupo / Nombre del Elemento", 225);
            _lvDuplicates.Columns.Add("Tamaño", 88, HorizontalAlignment.Right);
            _lvDuplicates.Columns.Add("Ahorro Grupo", 92, HorizontalAlignment.Right);
            _lvDuplicates.Columns.Add("Disco", 48, HorizontalAlignment.Center);
            _lvDuplicates.Columns.Add("📅 Fecha Agregado", 125);
            _lvDuplicates.Columns.Add("🛡️ ¿Es Fundamental?", 205);
            _lvDuplicates.Columns.Add("Tipo de Coincidencia", 165);
            _lvDuplicates.Columns.Add("Versión / Variante", 130);
            _lvDuplicates.Columns.Add("Ruta Completa", 260);
            _lvDuplicates.SelectedIndexChanged += OnLvDuplicatesSelectedIndexChanged;
            _lvDuplicates.DoubleClick += delegate { OpenCurrentSelectedInExplorer(); };
            _lvDuplicates.MouseUp += OnLvDuplicatesMouseUp;

            _tabDuplicates.Controls.Add(_lvDuplicates);
            _tabDuplicates.Controls.Add(_dupFilterPanel);

            // Tab 4: DEL MÁS VIEJO AL MÁS NUEVO (Cronológico por Disco)
            _tabChronological = new TabPage("⏳ Más Viejo a Nuevo");
            _tabChronological.BackColor = Color.FromArgb(15, 23, 42);

            _chronoFilterPanel = new Panel();
            _chronoFilterPanel.Dock = DockStyle.Top;
            _chronoFilterPanel.Height = 64;
            _chronoFilterPanel.BackColor = Color.FromArgb(30, 41, 59);
            _chronoFilterPanel.Padding = new Padding(8, 5, 8, 5);

            Panel chronoRow1 = new Panel { Dock = DockStyle.Top, Height = 26 };

            Label lblChronoDisk = new Label
            {
                Text = "Disco:",
                Dock = DockStyle.Left,
                Width = 42,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold)
            };

            _cmbChronoDiskFilter = new ComboBox();
            _cmbChronoDiskFilter.Dock = DockStyle.Left;
            _cmbChronoDiskFilter.Width = 108;
            _cmbChronoDiskFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbChronoDiskFilter.BackColor = Color.FromArgb(15, 23, 42);
            _cmbChronoDiskFilter.ForeColor = Color.White;
            _cmbChronoDiskFilter.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _cmbChronoDiskFilter.Items.Add("Todos los Discos");
            _cmbChronoDiskFilter.SelectedIndex = 0;
            _cmbChronoDiskFilter.SelectedIndexChanged += delegate
            {
                PopulateChronologicalList();
                if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
                    UpdateTreemapView();
            };

            Label lblChronoOrder = new Label
            {
                Text = " Orden:",
                Dock = DockStyle.Left,
                Width = 48,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold)
            };

            _cmbChronoOrder = new ComboBox();
            _cmbChronoOrder.Dock = DockStyle.Left;
            _cmbChronoOrder.Width = 175;
            _cmbChronoOrder.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbChronoOrder.BackColor = Color.FromArgb(15, 23, 42);
            _cmbChronoOrder.ForeColor = Color.White;
            _cmbChronoOrder.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _cmbChronoOrder.Items.AddRange(new object[]
            {
                "⏳ Más Viejo al Más Nuevo (▲)",
                "🆕 Más Nuevo al Más Viejo (▼)"
            });
            _cmbChronoOrder.SelectedIndex = 0;
            _cmbChronoOrder.SelectedIndexChanged += delegate
            {
                _chronoSortCol = 1;
                _chronoSortAsc = (_cmbChronoOrder.SelectedIndex == 0);
                PopulateChronologicalList();
                if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
                    UpdateTreemapView();
            };

            Label lblChronoScope = new Label
            {
                Text = " Ver:",
                Dock = DockStyle.Left,
                Width = 34,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold)
            };

            _cmbChronoScope = new ComboBox();
            _cmbChronoScope.Dock = DockStyle.Left;
            _cmbChronoScope.Width = 165;
            _cmbChronoScope.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbChronoScope.BackColor = Color.FromArgb(15, 23, 42);
            _cmbChronoScope.ForeColor = Color.White;
            _cmbChronoScope.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _cmbChronoScope.Items.AddRange(new object[]
            {
                "Archivos (Sin Windows interno)",
                "Solo Archivos Personales/Sueltos",
                "Solo Carpetas e Instalaciones",
                "Todo el Disco (Sin filtro)"
            });
            _cmbChronoScope.SelectedIndex = 0;
            _cmbChronoScope.SelectedIndexChanged += delegate
            {
                PopulateChronologicalList();
                if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
                    UpdateTreemapView();
            };

            _cmbChronoMinSize = new ComboBox();
            _cmbChronoMinSize.Dock = DockStyle.Left;
            _cmbChronoMinSize.Width = 86;
            _cmbChronoMinSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbChronoMinSize.BackColor = Color.FromArgb(15, 23, 42);
            _cmbChronoMinSize.ForeColor = Color.White;
            _cmbChronoMinSize.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _cmbChronoMinSize.Items.AddRange(new object[]
            {
                ">= 1 MB",
                ">= 10 MB",
                ">= 100 MB",
                "Todos (0 B+)"
            });
            _cmbChronoMinSize.SelectedIndex = 0;
            _cmbChronoMinSize.SelectedIndexChanged += delegate
            {
                PopulateChronologicalList();
                if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
                    UpdateTreemapView();
            };

            _btnDeleteChronoItem = CreateStyledButton("🔥 Borrar", Color.FromArgb(185, 28, 28), Color.White);
            _btnDeleteChronoItem.Dock = DockStyle.Right;
            _btnDeleteChronoItem.Width = 84;
            _btnDeleteChronoItem.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnDeleteChronoItem.Click += delegate { DeleteCurrentlySelectedItem(true); };

            Panel cSep0 = new Panel { Width = 4, Dock = DockStyle.Right };

            _btnOpenChronoFolder = CreateStyledButton("📂 Ubicación", Color.FromArgb(51, 65, 85), Color.White);
            _btnOpenChronoFolder.Dock = DockStyle.Right;
            _btnOpenChronoFolder.Width = 88;
            _btnOpenChronoFolder.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnOpenChronoFolder.Click += delegate { OpenCurrentSelectedInExplorer(); };

            chronoRow1.Controls.Add(_btnOpenChronoFolder);
            chronoRow1.Controls.Add(cSep0);
            chronoRow1.Controls.Add(_btnDeleteChronoItem);
            chronoRow1.Controls.Add(_cmbChronoMinSize);
            chronoRow1.Controls.Add(_cmbChronoScope);
            chronoRow1.Controls.Add(lblChronoScope);
            chronoRow1.Controls.Add(_cmbChronoOrder);
            chronoRow1.Controls.Add(lblChronoOrder);
            chronoRow1.Controls.Add(_cmbChronoDiskFilter);
            chronoRow1.Controls.Add(lblChronoDisk);

            _lblChronoSummary = new Label();
            _lblChronoSummary.Dock = DockStyle.Bottom;
            _lblChronoSummary.Height = 24;
            _lblChronoSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblChronoSummary.ForeColor = Color.FromArgb(250, 204, 21);
            _lblChronoSummary.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblChronoSummary.Text = "Ordenando archivos del más viejo al más nuevo...";

            _chronoFilterPanel.Controls.Add(chronoRow1);
            _chronoFilterPanel.Controls.Add(_lblChronoSummary);

            _lvChronological = CreateStyledListView();
            _lvChronological.Columns.Add("Nombre de Archivo / Carpeta", 205);
            _lvChronological.Columns.Add("📅 Fecha Agregado (Antigüedad)", 190);
            _lvChronological.Columns.Add("Tamaño", 90, HorizontalAlignment.Right);
            _lvChronological.Columns.Add("Disco", 48, HorizontalAlignment.Center);
            _lvChronological.Columns.Add("🛡️ ¿Es Fundamental? / Seguridad", 215);
            _lvChronological.Columns.Add("🕒 Última Modificación", 125);
            _lvChronological.Columns.Add("Categoría / Tipo", 135);
            _lvChronological.Columns.Add("Ruta Completa", 280);
            _lvChronological.SelectedIndexChanged += OnLvChronologicalSelectedIndexChanged;
            _lvChronological.DoubleClick += delegate { OpenCurrentSelectedInExplorer(); };
            _lvChronological.ColumnClick += OnLvChronologicalColumnClick;
            _lvChronological.MouseUp += OnLvChronologicalMouseUp;

            _tabChronological.Controls.Add(_lvChronological);
            _tabChronological.Controls.Add(_chronoFilterPanel);

            // Tab 5: Top Archivos Más Pesados
            _tabTopFiles = new TabPage("🔥 Top Archivos");
            _tabTopFiles.BackColor = Color.FromArgb(15, 23, 42);

            _lvTopFiles = CreateStyledListView();
            _lvTopFiles.Columns.Add("Nombre de Archivo", 205);
            _lvTopFiles.Columns.Add("Tamaño", 92, HorizontalAlignment.Right);
            _lvTopFiles.Columns.Add("Disco", 50, HorizontalAlignment.Center);
            _lvTopFiles.Columns.Add("📅 Fecha Agregado", 125);
            _lvTopFiles.Columns.Add("🛡️ ¿Es Fundamental? / Seguridad", 215);
            _lvTopFiles.Columns.Add("Categoría", 135);
            _lvTopFiles.Columns.Add("Extensión", 68);
            _lvTopFiles.Columns.Add("Ruta Completa", 290);
            _lvTopFiles.SelectedIndexChanged += OnLvTopFilesSelectedIndexChanged;
            _lvTopFiles.DoubleClick += delegate { OpenSelectedTopFileInExplorer(); };
            _lvTopFiles.ColumnClick += OnLvTopFilesColumnClick;
            _lvTopFiles.MouseUp += OnLvTopFilesMouseUp;
            _tabTopFiles.Controls.Add(_lvTopFiles);

            _tabLists.TabPages.Add(_tabFiles);
            _tabLists.TabPages.Add(_tabInstalls);
            _tabLists.TabPages.Add(_tabDuplicates);
            _tabLists.TabPages.Add(_tabChronological);
            _tabLists.TabPages.Add(_tabTopFiles);

            _splitMain.Panel2.Controls.Add(_tabLists);

            this.Controls.Add(_splitMain);
            this.Controls.Add(_toolbarPanel);
            this.Controls.Add(_topHeaderPanel);
            this.Controls.Add(_bottomStatusPanel);
        }

        private Panel CreateColorLegendPanel()
        {
            FlowLayoutPanel pnl = new FlowLayoutPanel();
            pnl.BackColor = Color.FromArgb(30, 41, 59);
            pnl.Padding = new Padding(8, 5, 8, 3);
            pnl.WrapContents = false;
            pnl.AutoScroll = false;

            AddLegendChip(pnl, "Instalaciones / Programas", FormatUtils.GetCategoryColor(ItemCategory.Instalacion));
            AddLegendChip(pnl, "Juegos / Librerías", FormatUtils.GetCategoryColor(ItemCategory.Juegos));
            AddLegendChip(pnl, "Videos / Audio", FormatUtils.GetCategoryColor(ItemCategory.VideoAudio));
            AddLegendChip(pnl, "Comprimidos / ISOs", FormatUtils.GetCategoryColor(ItemCategory.ComprimidosISO));
            AddLegendChip(pnl, "Documentos / Código", FormatUtils.GetCategoryColor(ItemCategory.DocumentosCodigo));
            AddLegendChip(pnl, "Imágenes", FormatUtils.GetCategoryColor(ItemCategory.Imagenes));
            AddLegendChip(pnl, "Sistema / Windows", FormatUtils.GetCategoryColor(ItemCategory.SistemaWindows));
            AddLegendChip(pnl, "Carpetas / Otros", FormatUtils.GetCategoryColor(ItemCategory.CarpetaGeneral));

            return pnl;
        }

        private void AddLegendChip(FlowLayoutPanel parent, string text, Color color)
        {
            Panel chip = new Panel();
            chip.Height = 18;
            chip.Width = TextRenderer.MeasureText(text, new Font("Segoe UI", 7.75f)).Width + 24;
            chip.Margin = new Padding(0, 0, 10, 0);

            Panel box = new Panel();
            box.Size = new Size(12, 12);
            box.Location = new Point(2, 3);
            box.BackColor = color;

            Label lbl = new Label();
            lbl.Text = text;
            lbl.Location = new Point(17, 1);
            lbl.AutoSize = true;
            lbl.Font = new Font("Segoe UI", 7.75f, FontStyle.Regular);
            lbl.ForeColor = Color.FromArgb(203, 213, 225);

            chip.Controls.Add(box);
            chip.Controls.Add(lbl);
            parent.Controls.Add(chip);
        }

        private Button CreateStyledButton(string text, Color bgColor, Color fgColor)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            btn.BackColor = bgColor;
            btn.ForeColor = fgColor;
            btn.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private ListView CreateStyledListView()
        {
            ListView lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.BackColor = Color.FromArgb(15, 23, 42);
            lv.ForeColor = Color.FromArgb(241, 245, 249);
            lv.BorderStyle = BorderStyle.None;
            lv.Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(lv, true, null);
            }
            catch { }

            return lv;
        }

        private void InitializeContextMenu()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Font = new Font("Segoe UI", 9f);

            ToolStripMenuItem miOpenExplorer = new ToolStripMenuItem("📂 Abrir ubicación en el Explorador de Windows");
            miOpenExplorer.Click += delegate { ExecuteContextAction("EXPLORER"); };

            ToolStripMenuItem miZoomIn = new ToolStripMenuItem("🔍 Entrar a esta carpeta / Ver contenido en Cuadros (Zoom)");
            miZoomIn.Click += delegate { ExecuteContextAction("ZOOM"); };

            ToolStripMenuItem miCopyPath = new ToolStripMenuItem("📋 Copiar ruta completa al portapapeles");
            miCopyPath.Click += delegate { ExecuteContextAction("COPY"); };

            ToolStripMenuItem miDeleteRecycle = new ToolStripMenuItem("🗑️ Enviar a la Papelera de Reciclaje (Recuperable)");
            miDeleteRecycle.ForeColor = Color.FromArgb(180, 83, 9);
            miDeleteRecycle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            miDeleteRecycle.Click += delegate { ExecuteContextAction("DELETE_RECYCLE"); };

            ToolStripMenuItem miDeletePermanent = new ToolStripMenuItem("🔥 Eliminar Permanentemente del Disco (Forzado como Administrador)");
            miDeletePermanent.ForeColor = Color.FromArgb(185, 28, 28);
            miDeletePermanent.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            miDeletePermanent.Click += delegate { ExecuteContextAction("DELETE_PERMANENT"); };

            ToolStripMenuItem miUninstall = new ToolStripMenuItem("🛠️ Ejecutar Desinstalador Oficial del Programa (si existe)");
            miUninstall.Click += delegate { ExecuteContextAction("UNINSTALL"); };

            ToolStripMenuItem miViewDuplicatesMap = new ToolStripMenuItem("🧬 Ver Mapa de Cuadros de REPETIDOS Y VERSIONES");
            miViewDuplicatesMap.Click += delegate { SetTreemapMode(TreemapViewMode.DuplicatesAndSimilarsMap, true); };

            ToolStripMenuItem miViewInstallsMap = new ToolStripMenuItem("💿 Ver Mapa de Cuadros de TODO LO INSTALADO");
            miViewInstallsMap.Click += delegate { SetTreemapMode(TreemapViewMode.InstallationsMap, true); };

            _contextMenu.Items.Add(miOpenExplorer);
            _contextMenu.Items.Add(miZoomIn);
            _contextMenu.Items.Add(miCopyPath);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(miDeleteRecycle);
            _contextMenu.Items.Add(miDeletePermanent);
            _contextMenu.Items.Add(miUninstall);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(miViewDuplicatesMap);
            _contextMenu.Items.Add(miViewInstallsMap);
        }

        private void LoadDisksAndStartInitialDetection()
        {
            _disks = NativeScanner.DetectAllDisks();

            _cmbInstallDiskFilter.Items.Clear();
            _cmbInstallDiskFilter.Items.Add("Todos los Discos");
            _cmbDupDiskFilter.Items.Clear();
            _cmbDupDiskFilter.Items.Add("Todos los Discos");
            _cmbChronoDiskFilter.Items.Clear();
            _cmbChronoDiskFilter.Items.Add("Todos los Discos");

            for (int i = 0; i < _disks.Count; i++)
            {
                _cmbInstallDiskFilter.Items.Add(_disks[i].DriveLetter);
                _cmbDupDiskFilter.Items.Add(_disks[i].DriveLetter);
                _cmbChronoDiskFilter.Items.Add(_disks[i].DriveLetter);
            }
            _cmbInstallDiskFilter.SelectedIndex = 0;
            _cmbDupDiskFilter.SelectedIndex = 0;
            _cmbChronoDiskFilter.SelectedIndex = 0;

            RebuildDiskCardsUI();

            _isScanningInstalls = true;
            Thread instThread = new Thread(delegate()
            {
                List<InstallItem> detected = InstallScanner.DetectAllInstallations(
                    _disks,
                    delegate(string msg)
                    {
                        SafeInvoke(delegate
                        {
                            if (!_isScanningDisk && !_isDeleting)
                                _lblStatusText.Text = msg;
                        });
                    },
                    delegate(List<InstallItem> initialList)
                    {
                        SafeInvoke(delegate
                        {
                            _allInstallations = initialList;
                            PopulateInstallationsList();
                            PopulateDuplicatesList(true);
                            if (_treemapMode == TreemapViewMode.InstallationsMap || _treemapMode == TreemapViewMode.DuplicatesAndSimilarsMap)
                            {
                                UpdateTreemapView();
                            }
                        });
                    });

                SafeInvoke(delegate
                {
                    _allInstallations = detected;
                    _isScanningInstalls = false;

                    for (int i = 0; i < _disks.Count; i++)
                    {
                        if (_disks[i].IsScanned)
                        {
                            InstallScanner.LinkInstallationsWithScannedDisk(_disks[i], _allInstallations);
                        }
                    }

                    PopulateInstallationsList();
                    PopulateDuplicatesList(true);
                    RefreshCurrentViews(false);

                    if (!_isScanningDisk && !_isDeleting)
                    {
                        _lblStatusText.Text = string.Format("🛡️ Listo • {0} discos • {1} instalaciones • {2} grupos de repetidos/similares detectados.",
                            _disks.Count, _allInstallations.Count, _allDuplicateGroups.Count);
                    }
                });
            });
            instThread.IsBackground = true;
            instThread.Start();

            if (_disks.Count > 0)
            {
                SelectDisk(_disks[0]);
            }
        }

        private void RebuildDiskCardsUI()
        {
            _disksFlowPanel.SuspendLayout();
            _disksFlowPanel.Controls.Clear();

            long totalAll = 0;
            long usedAll = 0;
            long freeAll = 0;
            int scannedCount = 0;
            for (int i = 0; i < _disks.Count; i++)
            {
                totalAll += _disks[i].TotalBytes;
                usedAll += _disks[i].UsedBytes;
                freeAll += _disks[i].FreeBytes;
                if (_disks[i].IsScanned) scannedCount++;
            }

            Panel allCard = CreateDiskCardControl(
                null,
                "🌐 TODOS LOS DISCOS",
                string.Format("{0} unidades ({1}/{0} escaneadas)", _disks.Count, scannedCount),
                usedAll,
                totalAll,
                freeAll,
                _selectedDisk == null);
            _disksFlowPanel.Controls.Add(allCard);

            for (int i = 0; i < _disks.Count; i++)
            {
                DiskItem d = _disks[i];
                string statusTag = d.IsScanning ? "⏳ Escaneando..." : (d.IsScanned ? "✔ Escaneado" : "Clic para escanear");
                string subtitle = string.Format("{0} • {1}", string.IsNullOrEmpty(d.DriveFormat) ? d.DriveTypeDesc : d.DriveFormat, statusTag);
                Panel card = CreateDiskCardControl(
                    d,
                    "💾 " + d.DisplayTitle,
                    subtitle,
                    d.UsedBytes,
                    d.TotalBytes,
                    d.FreeBytes,
                    _selectedDisk == d);
                _disksFlowPanel.Controls.Add(card);
            }

            _disksFlowPanel.ResumeLayout();
        }

        private Panel CreateDiskCardControl(
            DiskItem disk,
            string title,
            string subtitle,
            long usedBytes,
            long totalBytes,
            long freeBytes,
            bool isSelected)
        {
            Panel card = new Panel();
            card.Size = new Size(236, 74);
            card.Margin = new Padding(0, 0, 10, 0);
            card.Cursor = Cursors.Hand;
            card.BackColor = isSelected ? Color.FromArgb(30, 58, 138) : Color.FromArgb(30, 41, 59);

            double pct = totalBytes > 0 ? ((double)usedBytes * 100.0 / (double)totalBytes) : 0.0;
            Color barColor = pct >= 90.0
                ? Color.FromArgb(239, 68, 68)
                : (pct >= 75.0 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(59, 130, 246));

            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Color borderColor = isSelected ? Color.FromArgb(96, 165, 250) : Color.FromArgb(51, 65, 85);
                float borderW = isSelected ? 2f : 1f;
                using (Pen p = new Pen(borderColor, borderW))
                {
                    g.DrawRectangle(p, 1, 1, card.Width - 2, card.Height - 2);
                }

                using (Font fTitle = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (SolidBrush bTitle = new SolidBrush(Color.White))
                {
                    g.DrawString(title, fTitle, bTitle, new RectangleF(8, 5, card.Width - 16, 18));
                }

                using (Font fSub = new Font("Segoe UI", 7.5f, FontStyle.Regular))
                using (SolidBrush bSub = new SolidBrush(isSelected ? Color.FromArgb(191, 219, 254) : Color.FromArgb(148, 163, 184)))
                {
                    g.DrawString(subtitle, fSub, bSub, new RectangleF(8, 22, card.Width - 16, 15));
                }

                Rectangle barBg = new Rectangle(8, 41, card.Width - 16, 9);
                using (SolidBrush bBarBg = new SolidBrush(Color.FromArgb(15, 23, 42)))
                {
                    g.FillRectangle(bBarBg, barBg);
                }
                int fillW = (int)Math.Round((pct / 100.0) * barBg.Width);
                fillW = Math.Max(2, Math.Min(barBg.Width, fillW));
                using (SolidBrush bBarFill = new SolidBrush(barColor))
                {
                    g.FillRectangle(bBarFill, new Rectangle(barBg.X, barBg.Y, fillW, barBg.Height));
                }

                string capStr = string.Format("Usado: {0} / {1} ({2:0.0}%) • Libre: {3}",
                    FormatUtils.FormatBytes(usedBytes),
                    FormatUtils.FormatBytes(totalBytes),
                    pct,
                    FormatUtils.FormatBytes(freeBytes));
                using (Font fCap = new Font("Segoe UI", 7.25f, FontStyle.Regular))
                using (SolidBrush bCap = new SolidBrush(Color.FromArgb(226, 232, 240)))
                {
                    g.DrawString(capStr, fCap, bCap, new RectangleF(8, 53, card.Width - 16, 16));
                }
            };

            card.Click += delegate
            {
                SelectDisk(disk);
            };

            return card;
        }

        private void SelectDisk(DiskItem disk)
        {
            _selectedDisk = disk;
            if (disk != null)
            {
                _currentFolderNode = disk.RootNode;
                int idx = _cmbInstallDiskFilter.Items.IndexOf(disk.DriveLetter);
                if (idx >= 0) _cmbInstallDiskFilter.SelectedIndex = idx;
                int cIdx = _cmbChronoDiskFilter.Items.IndexOf(disk.DriveLetter);
                if (cIdx >= 0) _cmbChronoDiskFilter.SelectedIndex = cIdx;
            }
            else
            {
                _currentFolderNode = null;
                _cmbInstallDiskFilter.SelectedIndex = 0;
                _cmbChronoDiskFilter.SelectedIndex = 0;
            }

            RebuildDiskCardsUI();
            RefreshCurrentViews(false);

            if (disk != null && !disk.IsScanned && !disk.IsScanning && !_isScanningDisk)
            {
                StartScanForSelectedDisk(false);
            }
        }

        private void StartScanForSelectedDisk(bool forceRescan)
        {
            if (_isScanningDisk)
            {
                MessageBox.Show("Ya hay un escaneo de disco en progreso. Puedes detenerlo con el botón 'Detener Escaneo' si deseas cambiar.",
                    "Escaneo en Progreso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_selectedDisk == null)
            {
                StartScanAllDisks();
                return;
            }

            if (_selectedDisk.IsScanned && !forceRescan)
            {
                RefreshCurrentViews(false);
                return;
            }

            DiskItem targetDisk = _selectedDisk;
            _isScanningDisk = true;
            _cancelRequested = false;
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _btnCancelScan.Visible = true;
            _btnScanCurrent.Enabled = false;
            _btnScanAllDisks.Enabled = false;

            RebuildDiskCardsUI();
            _treemap.EmptyMessage = string.Format("⏳ Escaneando archivos y carpetas de {0}...", targetDisk.DisplayTitle);
            _treemap.Invalidate();

            Thread scanThread = new Thread(delegate()
            {
                Stopwatch sw = Stopwatch.StartNew();
                NativeScanner.ScanDisk(
                    targetDisk,
                    delegate { return _cancelRequested; },
                    delegate(string currentPath, long files, long folders, long bytes)
                    {
                        SafeInvoke(delegate
                        {
                            double pct = targetDisk.UsedBytes > 0 ? Math.Min(99.0, (double)bytes * 100.0 / (double)targetDisk.UsedBytes) : 0.0;
                            _lblStatusText.Text = string.Format("⏳ Escaneando {0} ({1:0.0}%) — {2} archivos | {3} carpetas | {4} analizados | {5}",
                                targetDisk.DriveLetter,
                                pct,
                                FormatUtils.FormatNumber(files),
                                FormatUtils.FormatNumber(folders),
                                FormatUtils.FormatBytes(bytes),
                                currentPath);
                        });
                    });

                sw.Stop();

                SafeInvoke(delegate
                {
                    _isScanningDisk = false;
                    _progressBar.Visible = false;
                    _btnCancelScan.Visible = false;
                    _btnScanCurrent.Enabled = true;
                    _btnScanAllDisks.Enabled = true;

                    if (_allInstallations != null && _allInstallations.Count > 0)
                    {
                        InstallScanner.LinkInstallationsWithScannedDisk(targetDisk, _allInstallations);
                        PopulateInstallationsList();
                    }

                    if (_selectedDisk == targetDisk)
                    {
                        _currentFolderNode = targetDisk.RootNode;
                    }

                    PopulateDuplicatesList(true);
                    RebuildDiskCardsUI();
                    RefreshCurrentViews(false);

                    _lblStatusText.Text = string.Format("✔ Disco {0} escaneado en {1:0.1} seg — {2} archivos, {3} carpetas ({4}) • {5} grupos repetidos.",
                        targetDisk.DisplayTitle,
                        sw.Elapsed.TotalSeconds,
                        FormatUtils.FormatNumber(targetDisk.ScannedFilesCount),
                        FormatUtils.FormatNumber(targetDisk.ScannedFoldersCount),
                        FormatUtils.FormatBytes(targetDisk.ScannedBytes),
                        _allDuplicateGroups.Count);
                });
            });
            scanThread.IsBackground = true;
            scanThread.Start();
        }

        private void StartScanAllDisks()
        {
            if (_isScanningDisk) return;

            _isScanningDisk = true;
            _cancelRequested = false;
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _btnCancelScan.Visible = true;
            _btnScanCurrent.Enabled = false;
            _btnScanAllDisks.Enabled = false;

            Thread allThread = new Thread(delegate()
            {
                Stopwatch swTotal = Stopwatch.StartNew();
                for (int i = 0; i < _disks.Count; i++)
                {
                    if (_cancelRequested) break;
                    DiskItem d = _disks[i];

                    SafeInvoke(delegate
                    {
                        RebuildDiskCardsUI();
                    });

                    NativeScanner.ScanDisk(
                        d,
                        delegate { return _cancelRequested; },
                        delegate(string currentPath, long files, long folders, long bytes)
                        {
                            SafeInvoke(delegate
                            {
                                double pct = d.UsedBytes > 0 ? Math.Min(99.0, (double)bytes * 100.0 / (double)d.UsedBytes) : 0.0;
                                _lblStatusText.Text = string.Format("⏳ [{0}/{1}] Escaneando {2} ({3:0.0}%) — {4} archivos | {5} analizados | {6}",
                                    i + 1,
                                    _disks.Count,
                                    d.DriveLetter,
                                    pct,
                                    FormatUtils.FormatNumber(files),
                                    FormatUtils.FormatBytes(bytes),
                                    currentPath);
                            });
                        });

                    SafeInvoke(delegate
                    {
                        if (_allInstallations != null && _allInstallations.Count > 0)
                        {
                            InstallScanner.LinkInstallationsWithScannedDisk(d, _allInstallations);
                        }
                        if (_selectedDisk == d && _currentFolderNode == null)
                        {
                            _currentFolderNode = d.RootNode;
                        }
                        PopulateDuplicatesList(true);
                        RebuildDiskCardsUI();
                        RefreshCurrentViews(false);
                    });
                }

                swTotal.Stop();

                SafeInvoke(delegate
                {
                    _isScanningDisk = false;
                    _progressBar.Visible = false;
                    _btnCancelScan.Visible = false;
                    _btnScanCurrent.Enabled = true;
                    _btnScanAllDisks.Enabled = true;

                    PopulateInstallationsList();
                    PopulateDuplicatesList(true);
                    RebuildDiskCardsUI();
                    RefreshCurrentViews(false);

                    _lblStatusText.Text = string.Format("✔ Todos los discos ({0}) escaneados en {1:0.1} seg • {2} instalaciones • {3} grupos de repetidos/versiones.",
                        _disks.Count,
                        swTotal.Elapsed.TotalSeconds,
                        _allInstallations.Count,
                        _allDuplicateGroups.Count);
                });
            });
            allThread.IsBackground = true;
            allThread.Start();
        }

        private void SetTreemapMode(TreemapViewMode mode, bool syncTab)
        {
            _treemapMode = mode;

            Color activeCol = Color.FromArgb(37, 99, 235);
            Color inactiveCol = Color.FromArgb(51, 65, 85);

            _btnModeHierarchy.BackColor = (mode == TreemapViewMode.HierarchyTwoLevels) ? activeCol : inactiveCol;
            _btnModeFlat.BackColor = (mode == TreemapViewMode.CurrentLevelFlat) ? activeCol : inactiveCol;
            _btnModeInstalls.BackColor = (mode == TreemapViewMode.InstallationsMap) ? activeCol : inactiveCol;
            _btnModeDuplicates.BackColor = (mode == TreemapViewMode.DuplicatesAndSimilarsMap) ? activeCol : inactiveCol;
            _btnModeChronological.BackColor = (mode == TreemapViewMode.ChronologicalAgeMap) ? activeCol : inactiveCol;
            _btnModeTopFiles.BackColor = (mode == TreemapViewMode.TopHeavyFilesMap) ? activeCol : inactiveCol;

            if (syncTab && !_suppressTabSync)
            {
                _suppressTabSync = true;
                try
                {
                    if (mode == TreemapViewMode.InstallationsMap && _tabLists.SelectedTab != _tabInstalls)
                    {
                        _tabLists.SelectedTab = _tabInstalls;
                    }
                    else if (mode == TreemapViewMode.DuplicatesAndSimilarsMap && _tabLists.SelectedTab != _tabDuplicates)
                    {
                        _tabLists.SelectedTab = _tabDuplicates;
                    }
                    else if (mode == TreemapViewMode.ChronologicalAgeMap && _tabLists.SelectedTab != _tabChronological)
                    {
                        _tabLists.SelectedTab = _tabChronological;
                    }
                    else if (mode == TreemapViewMode.TopHeavyFilesMap && _tabLists.SelectedTab != _tabTopFiles)
                    {
                        _tabLists.SelectedTab = _tabTopFiles;
                    }
                    else if ((mode == TreemapViewMode.HierarchyTwoLevels || mode == TreemapViewMode.CurrentLevelFlat) &&
                             _tabLists.SelectedTab != _tabFiles)
                    {
                        _tabLists.SelectedTab = _tabFiles;
                    }
                }
                finally
                {
                    _suppressTabSync = false;
                }
            }

            UpdateTreemapView();
            UpdateBreadcrumbLabel();
        }

        private void OnTabListsSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressTabSync) return;
            _suppressTabSync = true;
            try
            {
                if (_tabLists.SelectedTab == _tabInstalls)
                {
                    SetTreemapMode(TreemapViewMode.InstallationsMap, false);
                }
                else if (_tabLists.SelectedTab == _tabDuplicates)
                {
                    SetTreemapMode(TreemapViewMode.DuplicatesAndSimilarsMap, false);
                }
                else if (_tabLists.SelectedTab == _tabChronological)
                {
                    SetTreemapMode(TreemapViewMode.ChronologicalAgeMap, false);
                }
                else if (_tabLists.SelectedTab == _tabTopFiles)
                {
                    SetTreemapMode(TreemapViewMode.TopHeavyFilesMap, false);
                }
                else if (_tabLists.SelectedTab == _tabFiles)
                {
                    if (_treemapMode != TreemapViewMode.HierarchyTwoLevels &&
                        _treemapMode != TreemapViewMode.CurrentLevelFlat)
                    {
                        SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, false);
                    }
                }
            }
            finally
            {
                _suppressTabSync = false;
            }
        }

        private void RefreshCurrentViews(bool onlyTreemap)
        {
            UpdateBreadcrumbLabel();
            if (!onlyTreemap)
            {
                PopulateFilesList();
                PopulateInstallationsList();
                PopulateDuplicatesList(false);
                PopulateChronologicalList();
                PopulateTopFilesList();
            }
            UpdateTreemapView();
        }

        private void UpdateBreadcrumbLabel()
        {
            if (_treemapMode == TreemapViewMode.InstallationsMap)
            {
                string dFilter = _cmbInstallDiskFilter.SelectedItem != null ? _cmbInstallDiskFilter.SelectedItem.ToString() : "Todos los Discos";
                _lblBreadcrumb.Text = string.Format("💿 Mapa de Cuadros: TODO LO INSTALADO ({0})", dFilter);
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = true;
                return;
            }

            if (_treemapMode == TreemapViewMode.DuplicatesAndSimilarsMap)
            {
                string dFilter = _cmbDupDiskFilter.SelectedItem != null ? _cmbDupDiskFilter.SelectedItem.ToString() : "Todos los Discos";
                _lblBreadcrumb.Text = string.Format("🧬 Mapa de Cuadros: INSTALACIONES Y ARCHIVOS REPETIDOS O DE DISTINTA VERSIÓN ({0})", dFilter);
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = true;
                return;
            }

            if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
            {
                string dFilter = _cmbChronoDiskFilter.SelectedItem != null ? _cmbChronoDiskFilter.SelectedItem.ToString() : "Todos los Discos";
                string ord = _cmbChronoOrder.SelectedIndex == 0 ? "DEL MÁS VIEJO AL MÁS NUEVO" : "DEL MÁS NUEVO AL MÁS VIEJO";
                _lblBreadcrumb.Text = string.Format("⏳ Mapa de Cuadros por Antigüedad: {0} ({1})", ord, dFilter);
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = true;
                return;
            }

            if (_treemapMode == TreemapViewMode.TopHeavyFilesMap)
            {
                string dName = _selectedDisk != null ? _selectedDisk.DisplayTitle : "TODOS LOS DISCOS";
                _lblBreadcrumb.Text = string.Format("🔥 Mapa de Cuadros: Top Archivos Más Pesados en {0}", dName);
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = true;
                return;
            }

            if (_selectedDisk == null)
            {
                _lblBreadcrumb.Text = "🌐 Vista General: TODOS LOS DISCOS (Haz doble clic en un disco o carpeta para explorar)";
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = false;
                return;
            }

            if (_currentFolderNode != null)
            {
                _lblBreadcrumb.Text = string.Format("📍 {0}   ({1} • {2} archivos)",
                    _currentFolderNode.FullPath,
                    FormatUtils.FormatBytes(_currentFolderNode.Size),
                    FormatUtils.FormatNumber(_currentFolderNode.FileCount));
                _btnUpLevel.Enabled = (_currentFolderNode.Parent != null);
                _btnRootLevel.Enabled = (_currentFolderNode.Parent != null);
            }
            else
            {
                _lblBreadcrumb.Text = string.Format("📍 {0}", _selectedDisk.DisplayTitle);
                _btnUpLevel.Enabled = false;
                _btnRootLevel.Enabled = false;
            }
        }

        private void NavigateUpOneLevel()
        {
            if (_treemapMode != TreemapViewMode.HierarchyTwoLevels && _treemapMode != TreemapViewMode.CurrentLevelFlat)
            {
                SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
                return;
            }

            if (_currentFolderNode != null && _currentFolderNode.Parent != null)
            {
                _currentFolderNode = _currentFolderNode.Parent;
                RefreshCurrentViews(false);
            }
        }

        private void NavigateToDiskRoot()
        {
            if (_treemapMode != TreemapViewMode.HierarchyTwoLevels && _treemapMode != TreemapViewMode.CurrentLevelFlat)
            {
                SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
            }

            if (_selectedDisk != null && _selectedDisk.RootNode != null)
            {
                _currentFolderNode = _selectedDisk.RootNode;
                RefreshCurrentViews(false);
            }
        }

        private void UpdateTreemapView()
        {
            string search = (_txtSearch.Text ?? "").Trim();

            if (_treemapMode == TreemapViewMode.InstallationsMap)
            {
                BuildInstallationsTreemap(search);
                return;
            }

            if (_treemapMode == TreemapViewMode.DuplicatesAndSimilarsMap)
            {
                BuildDuplicatesTreemap();
                return;
            }

            if (_treemapMode == TreemapViewMode.ChronologicalAgeMap)
            {
                BuildChronologicalTreemap();
                return;
            }

            if (_treemapMode == TreemapViewMode.TopHeavyFilesMap)
            {
                BuildTopFilesTreemap(search);
                return;
            }

            if (_selectedDisk == null)
            {
                BuildAllDisksOverviewTreemap(search);
                return;
            }

            if (!_selectedDisk.IsScanned || _selectedDisk.RootNode == null)
            {
                _treemap.EmptyMessage = _selectedDisk.IsScanning
                    ? string.Format("⏳ Escaneando {0}... El mapa de cuadros aparecerá automáticamente al finalizar.", _selectedDisk.DisplayTitle)
                    : string.Format("Haz clic en '🔄 Escanear Disco Actual' para generar el mapa de cuadros de {0}.", _selectedDisk.DisplayTitle);
                _treemap.SetData(new List<TreemapItemInput>(), _selectedDisk.UsedBytes);
                return;
            }

            if (_currentFolderNode == null)
                _currentFolderNode = _selectedDisk.RootNode;

            List<TreemapItemInput> items = new List<TreemapItemInput>();
            List<FsNode> children = _currentFolderNode.Children;
            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    FsNode child = children[i];
                    if (child.Size <= 0) continue;
                    if (!string.IsNullOrEmpty(search) && !MatchesSearch(child, search))
                        continue;

                    TreemapItemInput ti = CreateNodeTreemapInput(child, _treemapMode == TreemapViewMode.HierarchyTwoLevels);
                    items.Add(ti);
                }
            }

            _treemap.EmptyMessage = "No hay archivos o carpetas que coincidan con el filtro actual.";
            long baseDiskBytes = _selectedDisk.RootNode != null && _selectedDisk.RootNode.Size > 0
                ? _selectedDisk.RootNode.Size
                : _selectedDisk.UsedBytes;
            _treemap.SetData(items, baseDiskBytes);
        }

        private TreemapItemInput CreateNodeTreemapInput(FsNode node, bool includeChildrenLevel)
        {
            TreemapItemInput ti = new TreemapItemInput();
            ti.Title = node.Name;
            ti.SizeBytes = node.Size;
            ti.Node = node;
            ti.Install = node.MatchedInstall;
            ti.Color = FormatUtils.GetCategoryColor(node.Category);

            if (node.MatchedInstall != null)
            {
                ti.Badge = "💿 INSTALACIÓN";
            }
            else if (!node.IsFolder && !string.IsNullOrEmpty(node.Extension))
            {
                ti.Badge = node.Extension.ToUpperInvariant();
            }

            if (includeChildrenLevel && node.IsFolder && node.Children != null && node.Children.Count > 0)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    FsNode sub = node.Children[i];
                    if (sub.Size <= 0) continue;
                    TreemapItemInput subTi = new TreemapItemInput();
                    subTi.Title = sub.Name;
                    subTi.SizeBytes = sub.Size;
                    subTi.Node = sub;
                    subTi.Install = sub.MatchedInstall;
                    subTi.Color = FormatUtils.GetCategoryColor(sub.Category);
                    if (sub.MatchedInstall != null)
                        subTi.Badge = "💿 INSTALADO";
                    ti.Children.Add(subTi);
                }
            }

            return ti;
        }

        private void BuildAllDisksOverviewTreemap(string search)
        {
            List<TreemapItemInput> diskGroups = new List<TreemapItemInput>();
            long totalUsedAll = 0;

            for (int i = 0; i < _disks.Count; i++)
            {
                DiskItem d = _disks[i];
                totalUsedAll += d.UsedBytes;

                TreemapItemInput dGroup = new TreemapItemInput();
                dGroup.Title = "💾 " + d.DisplayTitle;
                dGroup.Subtitle = string.Format("Usado: {0} de {1} ({2:0.1}%)",
                    FormatUtils.FormatBytes(d.UsedBytes),
                    FormatUtils.FormatBytes(d.TotalBytes),
                    d.UsedPercent);
                dGroup.SizeBytes = d.IsScanned && d.RootNode != null ? d.RootNode.Size : d.UsedBytes;
                dGroup.Color = Color.FromArgb(37, 99, 235);
                dGroup.Node = d.RootNode;
                dGroup.Badge = d.DriveLetter;

                if (d.IsScanned && d.RootNode != null && d.RootNode.Children != null)
                {
                    for (int c = 0; c < d.RootNode.Children.Count; c++)
                    {
                        FsNode child = d.RootNode.Children[c];
                        if (child.Size <= 0) continue;
                        if (!string.IsNullOrEmpty(search) && !MatchesSearch(child, search))
                            continue;
                        dGroup.Children.Add(CreateNodeTreemapInput(child, false));
                    }
                }

                diskGroups.Add(dGroup);
            }

            _treemap.EmptyMessage = "Haz clic en '⚡ Escanear TODOS los Discos' para ver el detalle completo de todos tus discos.";
            _treemap.SetData(diskGroups, totalUsedAll);
        }

        private void BuildInstallationsTreemap(string search)
        {
            if (_allInstallations == null || _allInstallations.Count == 0)
            {
                _treemap.EmptyMessage = _isScanningInstalls
                    ? "⏳ Detectando programas y juegos instalados en todos los discos..."
                    : "No se encontraron instalaciones que coincidan con el filtro.";
                _treemap.SetData(new List<TreemapItemInput>(), 1);
                return;
            }

            string diskFilter = _cmbInstallDiskFilter.SelectedItem != null ? _cmbInstallDiskFilter.SelectedItem.ToString() : "Todos los Discos";
            bool groupByDisk = (diskFilter == "Todos los Discos");

            if (groupByDisk)
            {
                Dictionary<string, TreemapItemInput> diskGroups = new Dictionary<string, TreemapItemInput>(StringComparer.OrdinalIgnoreCase);
                long totalBytes = 0;

                for (int i = 0; i < _displayedInstallItems.Count; i++)
                {
                    InstallItem inst = _displayedInstallItems[i];
                    long sz = Math.Max(inst.SizeBytes, 1024L * 1024L);
                    string dl = string.IsNullOrEmpty(inst.DriveLetter) ? "C:" : inst.DriveLetter.ToUpperInvariant();

                    TreemapItemInput grp;
                    if (!diskGroups.TryGetValue(dl, out grp))
                    {
                        grp = new TreemapItemInput();
                        grp.Title = "💿 Instalaciones en Disco " + dl;
                        grp.Badge = dl;
                        grp.Color = Color.FromArgb(30, 64, 175);
                        diskGroups[dl] = grp;
                    }

                    TreemapItemInput child = new TreemapItemInput();
                    child.Title = inst.Name;
                    child.Subtitle = inst.SourceType + (string.IsNullOrEmpty(inst.InstallLocation) ? "" : (" • " + inst.InstallLocation));
                    child.Badge = dl;
                    child.SizeBytes = sz;
                    child.Install = inst;
                    child.Node = inst.LinkedNode;
                    child.Color = GetInstallationColor(inst);

                    grp.Children.Add(child);
                    grp.SizeBytes += sz;
                    totalBytes += sz;
                }

                List<TreemapItemInput> groupsList = new List<TreemapItemInput>(diskGroups.Values);
                _treemap.EmptyMessage = "No hay instalaciones que coincidan con el filtro actual.";
                _treemap.SetData(groupsList, totalBytes);
            }
            else
            {
                List<TreemapItemInput> items = new List<TreemapItemInput>();
                long totalBytes = 0;
                for (int i = 0; i < _displayedInstallItems.Count; i++)
                {
                    InstallItem inst = _displayedInstallItems[i];
                    long sz = Math.Max(inst.SizeBytes, 1024L * 1024L);
                    TreemapItemInput ti = new TreemapItemInput();
                    ti.Title = inst.Name;
                    ti.Subtitle = inst.SourceType;
                    ti.Badge = inst.DriveLetter;
                    ti.SizeBytes = sz;
                    ti.Install = inst;
                    ti.Node = inst.LinkedNode;
                    ti.Color = GetInstallationColor(inst);
                    items.Add(ti);
                    totalBytes += sz;
                }
                _treemap.EmptyMessage = "No hay instalaciones en este disco que coincidan con el filtro.";
                _treemap.SetData(items, totalBytes);
            }
        }

        private void BuildDuplicatesTreemap()
        {
            List<TreemapItemInput> groupInputs = new List<TreemapItemInput>();
            long totalDupBytes = 0;

            for (int i = 0; i < _displayedDuplicateGroups.Count; i++)
            {
                DuplicateGroup grp = _displayedDuplicateGroups[i];
                long grpWeight = Math.Max(grp.TotalSizeBytes, grp.Entries.Count * 15L * 1024L * 1024L);

                TreemapItemInput gInput = new TreemapItemInput();
                gInput.Title = string.Format("{0} ({1} copias • Ahorro: {2})",
                    grp.DisplayGroupTitle,
                    grp.Entries.Count,
                    FormatUtils.FormatBytes(grp.PotentialSavingsBytes));
                gInput.Badge = string.Format("{0}x", grp.Entries.Count);
                gInput.SizeBytes = grpWeight;
                gInput.Color = GetDuplicateKindColor(grp.Kind);

                for (int j = 0; j < grp.Entries.Count; j++)
                {
                    DuplicateEntry entry = grp.Entries[j];
                    long entryWeight = Math.Max(entry.SizeBytes, 15L * 1024L * 1024L);

                    TreemapItemInput cInput = new TreemapItemInput();
                    cInput.Title = string.Format("[{0}] {1}", entry.DriveLetter, entry.Name);
                    cInput.Subtitle = string.Format("{0} • {1} • {2}",
                        grp.KindLabel,
                        entry.VersionOrVariant,
                        entry.FullPath);
                    cInput.Badge = entry.DriveLetter + " • " + (string.IsNullOrEmpty(entry.VersionOrVariant) ? "COPIA" : entry.VersionOrVariant);
                    cInput.SizeBytes = entryWeight;
                    cInput.Node = entry.Node;
                    cInput.Install = entry.Install;
                    cInput.Color = GetDuplicateKindColor(grp.Kind);
                    gInput.Children.Add(cInput);
                }

                groupInputs.Add(gInput);
                totalDupBytes += grpWeight;
            }

            _treemap.EmptyMessage = "No se encontraron instalaciones ni archivos repetidos con el filtro actual. (Usa '⚡ Escanear TODOS los Discos' para analizar todos los archivos).";
            _treemap.SetData(groupInputs, totalDupBytes);
        }

        private Color GetDuplicateKindColor(DuplicateMatchKind kind)
        {
            switch (kind)
            {
                case DuplicateMatchKind.RepeatedInstallation:
                    return Color.FromArgb(139, 92, 246); // Purple - Instalaciones repetidas / versiones
                case DuplicateMatchKind.IdenticalFile:
                    return Color.FromArgb(239, 68, 68);  // Red - Archivos idénticos repetidos
                case DuplicateMatchKind.VersionedOrSimilarFile:
                    return Color.FromArgb(245, 158, 11); // Amber - Archivos de distinta versión
                case DuplicateMatchKind.SimilarFolder:
                    return Color.FromArgb(59, 130, 246); // Blue - Carpetas repetidas / similares
                default:
                    return Color.FromArgb(100, 116, 139);
            }
        }

        private void BuildTopFilesTreemap(string search)
        {
            List<TreemapItemInput> items = new List<TreemapItemInput>();
            long totalBytes = 0;
            int limit = Math.Min(120, _displayedTopFiles.Count);
            for (int i = 0; i < limit; i++)
            {
                FsNode f = _displayedTopFiles[i];
                if (f.Size <= 0) continue;
                TreemapItemInput ti = new TreemapItemInput();
                ti.Title = f.Name;
                ti.Subtitle = f.FullPath;
                ti.Badge = f.DriveLetter + " " + (f.Extension ?? "");
                ti.SizeBytes = f.Size;
                ti.Node = f;
                ti.Color = FormatUtils.GetCategoryColor(f.Category);
                items.Add(ti);
                totalBytes += f.Size;
            }

            _treemap.EmptyMessage = "Escanea un disco primero para ver los cuadros de los archivos individuales más pesados.";
            _treemap.SetData(items, totalBytes);
        }

        private Color GetInstallationColor(InstallItem inst)
        {
            if (inst == null) return FormatUtils.GetCategoryColor(ItemCategory.Instalacion);
            if (inst.Category == ItemCategory.Juegos || (inst.SourceType != null && inst.SourceType.IndexOf("Juego", StringComparison.OrdinalIgnoreCase) >= 0))
                return FormatUtils.GetCategoryColor(ItemCategory.Juegos);
            if (inst.SourceType != null && inst.SourceType.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(20, 184, 166);
            if (inst.SourceType != null && inst.SourceType.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(16, 185, 129);
            if (inst.SourceType != null && (inst.SourceType.IndexOf("Carpeta", StringComparison.OrdinalIgnoreCase) >= 0 || inst.SourceType.IndexOf("Portable", StringComparison.OrdinalIgnoreCase) >= 0))
                return Color.FromArgb(245, 158, 11);
            return FormatUtils.GetCategoryColor(ItemCategory.Instalacion);
        }

        private bool MatchesSearch(FsNode node, string search)
        {
            if (string.IsNullOrEmpty(search) || node == null) return true;
            if (node.Name != null && node.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (node.FullPath != null && node.FullPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (node.MatchedInstall != null && node.MatchedInstall.Name != null &&
                node.MatchedInstall.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private void PopulateFilesList()
        {
            _displayedFileNodes.Clear();
            string search = (_txtSearch.Text ?? "").Trim();

            if (_selectedDisk == null)
            {
                for (int d = 0; d < _disks.Count; d++)
                {
                    if (_disks[d].IsScanned && _disks[d].RootNode != null && _disks[d].RootNode.Children != null)
                    {
                        for (int i = 0; i < _disks[d].RootNode.Children.Count; i++)
                        {
                            FsNode c = _disks[d].RootNode.Children[i];
                            if (MatchesSearch(c, search))
                                _displayedFileNodes.Add(c);
                        }
                    }
                }
            }
            else if (_currentFolderNode != null && _currentFolderNode.Children != null)
            {
                for (int i = 0; i < _currentFolderNode.Children.Count; i++)
                {
                    FsNode c = _currentFolderNode.Children[i];
                    if (MatchesSearch(c, search))
                        _displayedFileNodes.Add(c);
                }
            }

            SortFileNodesList();

            long diskBaseBytes = (_selectedDisk != null && _selectedDisk.RootNode != null && _selectedDisk.RootNode.Size > 0)
                ? _selectedDisk.RootNode.Size
                : (_selectedDisk != null ? _selectedDisk.UsedBytes : 1);

            _lvFiles.BeginUpdate();
            _lvFiles.Items.Clear();

            int limit = Math.Min(1500, _displayedFileNodes.Count);
            for (int i = 0; i < limit; i++)
            {
                FsNode node = _displayedFileNodes[i];
                string iconPrefix = node.MatchedInstall != null
                    ? "💿 "
                    : (node.IsFolder ? "📁 " : "📄 ");

                ListViewItem lvi = new ListViewItem(iconPrefix + node.Name);
                lvi.Tag = node;

                double pct = diskBaseBytes > 0 ? ((double)node.Size * 100.0 / (double)diskBaseBytes) : 0.0;
                string typeDesc = node.MatchedInstall != null
                    ? ("INSTALACIÓN (" + node.MatchedInstall.SourceType + ")")
                    : (node.IsFolder ? "Carpeta" : ("Archivo " + (node.Extension ?? "")));

                SafetyAssessment sa = SafetyAdvisor.AssessNode(node);
                DateTime dateAdded = node.CreatedDate.Year > 1980 ? node.CreatedDate : node.LastModified;

                lvi.SubItems.Add(FormatUtils.FormatBytes(node.Size));
                lvi.SubItems.Add(string.Format("{0:0.1}%", pct));
                lvi.SubItems.Add(SafetyAdvisor.FormatShortDate(dateAdded));
                lvi.SubItems.Add(sa.VerdictBadge);
                lvi.SubItems.Add(typeDesc);
                lvi.SubItems.Add(FormatUtils.FormatNumber(node.FileCount));
                lvi.SubItems.Add(node.FullPath ?? "");

                if (node.MatchedInstall != null)
                {
                    lvi.ForeColor = Color.FromArgb(147, 197, 253);
                }
                else if (node.Category == ItemCategory.Juegos)
                {
                    lvi.ForeColor = Color.FromArgb(216, 180, 254);
                }
                else if (node.Size >= 5L * 1024L * 1024L * 1024L)
                {
                    lvi.ForeColor = Color.FromArgb(253, 224, 71);
                }

                _lvFiles.Items.Add(lvi);
            }
            _lvFiles.EndUpdate();

            _tabFiles.Text = string.Format("📁 Archivos ({0})", _displayedFileNodes.Count);
        }

        private void PopulateInstallationsList()
        {
            _displayedInstallItems.Clear();
            string search = (_txtSearch.Text ?? "").Trim();
            string diskFilter = _cmbInstallDiskFilter.SelectedItem != null ? _cmbInstallDiskFilter.SelectedItem.ToString() : "Todos los Discos";
            int typeFilterIdx = _cmbInstallTypeFilter.SelectedIndex;

            long totalFilteredBytes = 0;

            if (_allInstallations != null)
            {
                for (int i = 0; i < _allInstallations.Count; i++)
                {
                    InstallItem inst = _allInstallations[i];

                    if (diskFilter != "Todos los Discos" &&
                        !string.Equals(inst.DriveLetter, diskFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (typeFilterIdx == 1 && (inst.SourceType == null || inst.SourceType.IndexOf("Registro", StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    if (typeFilterIdx == 2 && inst.Category != ItemCategory.Juegos && (inst.SourceType == null || inst.SourceType.IndexOf("Juego", StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    if (typeFilterIdx == 3 && (inst.SourceType == null || (inst.SourceType.IndexOf("Carpeta", StringComparison.OrdinalIgnoreCase) < 0 && inst.SourceType.IndexOf("Portable", StringComparison.OrdinalIgnoreCase) < 0)))
                        continue;
                    if (typeFilterIdx == 4 && (inst.SourceType == null || inst.SourceType.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    if (typeFilterIdx == 5 && (inst.SourceType == null || inst.SourceType.IndexOf("Store", StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    if (!string.IsNullOrEmpty(search))
                    {
                        bool match = (inst.Name != null && inst.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                     (inst.Publisher != null && inst.Publisher.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                     (inst.InstallLocation != null && inst.InstallLocation.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                     (inst.SourceType != null && inst.SourceType.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!match) continue;
                    }

                    _displayedInstallItems.Add(inst);
                    totalFilteredBytes += inst.SizeBytes;
                }
            }

            SortInstallItemsList();

            _lvInstalls.BeginUpdate();
            _lvInstalls.Items.Clear();

            for (int i = 0; i < _displayedInstallItems.Count; i++)
            {
                InstallItem inst = _displayedInstallItems[i];
                string prefix = inst.Category == ItemCategory.Juegos ? "🎮 " : "💿 ";
                ListViewItem lvi = new ListViewItem(prefix + inst.Name);
                lvi.Tag = inst;

                string sizeText = inst.SizeBytes > 0 ? FormatUtils.FormatBytes(inst.SizeBytes) : "N/D";
                SafetyAssessment saInst = SafetyAdvisor.AssessInstallation(inst);
                string dateInst = inst.CreatedDate.Year > 1980
                    ? SafetyAdvisor.FormatShortDate(inst.CreatedDate)
                    : (!string.IsNullOrEmpty(inst.InstallDate) ? inst.InstallDate : "N/D");

                lvi.SubItems.Add(sizeText);
                lvi.SubItems.Add(inst.DriveLetter ?? "");
                lvi.SubItems.Add(dateInst);
                lvi.SubItems.Add(saInst.VerdictBadge);
                lvi.SubItems.Add(inst.SourceType ?? "");
                lvi.SubItems.Add(inst.Publisher ?? "");
                lvi.SubItems.Add(inst.InstallLocation ?? "");

                if (inst.SizeBytes >= 10L * 1024L * 1024L * 1024L)
                {
                    lvi.ForeColor = Color.FromArgb(253, 224, 71);
                }
                else if (inst.Category == ItemCategory.Juegos)
                {
                    lvi.ForeColor = Color.FromArgb(216, 180, 254);
                }

                _lvInstalls.Items.Add(lvi);
            }
            _lvInstalls.EndUpdate();

            _lblInstallSummary.Text = string.Format("💿 Mostrando {0} instalaciones ({1}) • Peso total: {2}   |   Usa '🔥 Borrar / Desinstalar' o clic derecho para eliminar",
                _displayedInstallItems.Count,
                diskFilter,
                FormatUtils.FormatBytes(totalFilteredBytes));

            _tabInstalls.Text = string.Format("💿 Instalado ({0})", _displayedInstallItems.Count);
        }

        private void PopulateDuplicatesList(bool recomputeAnalysis)
        {
            if (recomputeAnalysis || _allDuplicateGroups == null)
            {
                _allDuplicateGroups = DuplicateScanner.AnalyzeAllDuplicatesAndSimilars(_disks, _allInstallations);
            }

            _displayedDuplicateGroups.Clear();
            _displayedDuplicateEntries.Clear();

            string search = (_txtSearch.Text ?? "").Trim();
            string diskFilter = _cmbDupDiskFilter.SelectedItem != null ? _cmbDupDiskFilter.SelectedItem.ToString() : "Todos los Discos";
            int kindFilterIdx = _cmbDupKindFilter.SelectedIndex;

            long totalPotentialSavings = 0;
            int totalEntriesCount = 0;

            for (int g = 0; g < _allDuplicateGroups.Count; g++)
            {
                DuplicateGroup grp = _allDuplicateGroups[g];

                if (kindFilterIdx == 1 && grp.Kind != DuplicateMatchKind.RepeatedInstallation) continue;
                if (kindFilterIdx == 2 && grp.Kind != DuplicateMatchKind.IdenticalFile) continue;
                if (kindFilterIdx == 3 && grp.Kind != DuplicateMatchKind.VersionedOrSimilarFile) continue;
                if (kindFilterIdx == 4 && grp.Kind != DuplicateMatchKind.SimilarFolder) continue;

                if (diskFilter != "Todos los Discos")
                {
                    bool hasEntryInDisk = false;
                    for (int e = 0; e < grp.Entries.Count; e++)
                    {
                        if (string.Equals(grp.Entries[e].DriveLetter, diskFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            hasEntryInDisk = true;
                            break;
                        }
                    }
                    if (!hasEntryInDisk) continue;
                }

                if (!string.IsNullOrEmpty(search))
                {
                    bool match = grp.DisplayGroupTitle != null && grp.DisplayGroupTitle.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                    {
                        for (int e = 0; e < grp.Entries.Count; e++)
                        {
                            DuplicateEntry de = grp.Entries[e];
                            if ((de.Name != null && de.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (de.FullPath != null && de.FullPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                match = true;
                                break;
                            }
                        }
                    }
                    if (!match) continue;
                }

                _displayedDuplicateGroups.Add(grp);
                totalPotentialSavings += grp.PotentialSavingsBytes;
                totalEntriesCount += grp.Entries.Count;
            }

            _lvDuplicates.BeginUpdate();
            _lvDuplicates.Items.Clear();

            for (int g = 0; g < _displayedDuplicateGroups.Count; g++)
            {
                DuplicateGroup grp = _displayedDuplicateGroups[g];
                bool evenGroup = (g % 2 == 0);
                Color groupBg = evenGroup ? Color.FromArgb(15, 23, 42) : Color.FromArgb(24, 35, 58);

                for (int e = 0; e < grp.Entries.Count; e++)
                {
                    DuplicateEntry entry = grp.Entries[e];
                    _displayedDuplicateEntries.Add(entry);

                    string prefix = string.Format("[Grupo #{0}: {1}]  {2}",
                        g + 1,
                        grp.DisplayGroupTitle,
                        entry.Name);

                    ListViewItem lvi = new ListViewItem(prefix);
                    lvi.Tag = entry;
                    lvi.BackColor = groupBg;

                    SafetyAssessment saDup = entry.Install != null
                        ? SafetyAdvisor.AssessInstallation(entry.Install)
                        : (entry.Node != null ? SafetyAdvisor.AssessNode(entry.Node) : SafetyAdvisor.AssessPathAndName(entry.Name, entry.FullPath, "", false, ItemCategory.Instalacion));

                    DateTime dtDup = entry.CreatedDate.Year > 1980 ? entry.CreatedDate : entry.LastModified;
                    string dateDupStr = dtDup.Year > 1980 ? SafetyAdvisor.FormatShortDate(dtDup) : (entry.Install != null && !string.IsNullOrEmpty(entry.Install.InstallDate) ? entry.Install.InstallDate : "N/D");

                    lvi.SubItems.Add(entry.SizeBytes > 0 ? FormatUtils.FormatBytes(entry.SizeBytes) : "N/D");
                    lvi.SubItems.Add(e == 0 ? ("+" + FormatUtils.FormatBytes(grp.PotentialSavingsBytes)) : "↳ (mismo grupo)");
                    lvi.SubItems.Add(entry.DriveLetter ?? "");
                    lvi.SubItems.Add(dateDupStr);
                    lvi.SubItems.Add(saDup.VerdictBadge);
                    lvi.SubItems.Add(string.Format("{0} ({1} copias)", grp.KindLabel, grp.Entries.Count));
                    lvi.SubItems.Add(entry.VersionOrVariant ?? "");
                    lvi.SubItems.Add(entry.FullPath ?? "");

                    if (grp.Kind == DuplicateMatchKind.RepeatedInstallation)
                        lvi.ForeColor = Color.FromArgb(216, 180, 254); // Purple
                    else if (grp.Kind == DuplicateMatchKind.IdenticalFile)
                        lvi.ForeColor = Color.FromArgb(252, 165, 165); // Soft red
                    else if (grp.Kind == DuplicateMatchKind.VersionedOrSimilarFile)
                        lvi.ForeColor = Color.FromArgb(253, 224, 71);  // Gold
                    else
                        lvi.ForeColor = Color.FromArgb(147, 197, 253); // Soft blue

                    _lvDuplicates.Items.Add(lvi);
                }
            }
            _lvDuplicates.EndUpdate();

            _lblDupSummary.Text = string.Format("🧬 Detectados {0} grupos de repetidos/versiones ({1} elementos) • Espacio extra recuperable (Ahorro potencial): {2}",
                _displayedDuplicateGroups.Count,
                totalEntriesCount,
                FormatUtils.FormatBytes(totalPotentialSavings));

            _tabDuplicates.Text = string.Format("🧬 Repetidos / Versiones ({0})", _displayedDuplicateGroups.Count);
        }

        private static DateTime GetEffectiveDate(FsNode n)
        {
            if (n == null) return DateTime.MinValue;
            if (n.CreatedDate.Year > 1985 && n.LastModified.Year > 1985)
            {
                // Use the older of CreatedDate and LastModified as the true original age of the file
                return n.CreatedDate <= n.LastModified ? n.CreatedDate : n.LastModified;
            }
            if (n.CreatedDate.Year > 1985) return n.CreatedDate;
            if (n.LastModified.Year > 1985) return n.LastModified;
            return DateTime.MinValue;
        }

        private void PopulateChronologicalList()
        {
            _displayedChronoNodes.Clear();
            string search = (_txtSearch.Text ?? "").Trim();
            string diskFilter = _cmbChronoDiskFilter.SelectedItem != null ? _cmbChronoDiskFilter.SelectedItem.ToString() : "Todos los Discos";
            int scopeIdx = _cmbChronoScope.SelectedIndex; // 0: Sin Windows interno, 1: Solo Archivos Personales/Sueltos, 2: Solo Carpetas e Instalaciones, 3: Todo
            int minSizeIdx = _cmbChronoMinSize.SelectedIndex; // 0: >=1MB, 1: >=10MB, 2: >=100MB, 3: 0B+

            long minBytes = 1024L * 1024L;
            if (minSizeIdx == 1) minBytes = 10L * 1024L * 1024L;
            else if (minSizeIdx == 2) minBytes = 100L * 1024L * 1024L;
            else if (minSizeIdx == 3) minBytes = 0L;

            HashSet<string> claimedInstallPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_allInstallations != null)
            {
                for (int i = 0; i < _allInstallations.Count; i++)
                {
                    string loc = FormatUtils.NormalizePath(_allInstallations[i].InstallLocation);
                    if (!string.IsNullOrEmpty(loc) && loc.Length > 3)
                        claimedInstallPaths.Add(loc);
                }
            }

            for (int d = 0; d < _disks.Count; d++)
            {
                DiskItem disk = _disks[d];
                if (diskFilter != "Todos los Discos" &&
                    !string.Equals(disk.DriveLetter, diskFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                CollectChronologicalCandidatesForDisk(disk, scopeIdx, minBytes, search, claimedInstallPaths, _displayedChronoNodes);
            }

            SortChronoNodesList();

            _lvChronological.BeginUpdate();
            _lvChronological.Items.Clear();

            int limit = Math.Min(1200, _displayedChronoNodes.Count);
            DateTime oldestDate = DateTime.MaxValue;
            DateTime newestDate = DateTime.MinValue;
            long totalShownBytes = 0;

            for (int i = 0; i < limit; i++)
            {
                FsNode node = _displayedChronoNodes[i];
                DateTime effDate = GetEffectiveDate(node);
                if (effDate.Year > 1985)
                {
                    if (effDate < oldestDate) oldestDate = effDate;
                    if (effDate > newestDate) newestDate = effDate;
                }
                totalShownBytes += node.Size;

                string iconPrefix = node.MatchedInstall != null
                    ? "💿 "
                    : (node.IsFolder ? "📁 " : "📄 ");

                ListViewItem lvi = new ListViewItem(iconPrefix + node.Name);
                lvi.Tag = node;

                SafetyAssessment sa = SafetyAdvisor.AssessNode(node);

                lvi.SubItems.Add(SafetyAdvisor.FormatDateWithRelativeAge(effDate));
                lvi.SubItems.Add(FormatUtils.FormatBytes(node.Size));
                lvi.SubItems.Add(node.DriveLetter ?? "");
                lvi.SubItems.Add(sa.VerdictBadge);
                lvi.SubItems.Add(SafetyAdvisor.FormatShortDate(node.LastModified));
                lvi.SubItems.Add(node.MatchedInstall != null ? ("Instalación (" + node.MatchedInstall.SourceType + ")") : (node.IsFolder ? "Carpeta" : ("Archivo " + (node.Extension ?? ""))));
                lvi.SubItems.Add(node.FullPath ?? "");

                TimeSpan age = DateTime.Now - effDate;
                if (age.TotalDays >= 730) // >= 2 years old
                    lvi.ForeColor = Color.FromArgb(252, 165, 165); // Soft Coral Red for very old
                else if (age.TotalDays >= 365) // 1 - 2 years old
                    lvi.ForeColor = Color.FromArgb(253, 224, 71);  // Amber Gold
                else if (age.TotalDays <= 30) // Recent (< 30 days)
                    lvi.ForeColor = Color.FromArgb(167, 243, 208); // Mint Green

                _lvChronological.Items.Add(lvi);
            }
            _lvChronological.EndUpdate();

            string rangeInfo = (oldestDate < DateTime.MaxValue && newestDate > DateTime.MinValue)
                ? string.Format("Desde {0:dd/MM/yyyy} hasta {1:dd/MM/yyyy}", oldestDate, newestDate)
                : "Escanea el disco para ver todos los archivos";

            _lblChronoSummary.Text = string.Format("⏳ Mostrando {0} elementos en {1} ({2}) • Peso listado: {3}",
                limit,
                diskFilter,
                rangeInfo,
                FormatUtils.FormatBytes(totalShownBytes));

            _tabChronological.Text = string.Format("⏳ Más Viejo a Nuevo ({0})", limit);
        }

        private void CollectChronologicalCandidatesForDisk(
            DiskItem disk,
            int scopeIdx,
            long minBytes,
            string search,
            HashSet<string> claimedInstallPaths,
            List<FsNode> outList)
        {
            if (disk == null) return;

            if (disk.IsScanned && disk.RootNode != null)
            {
                Stack<KeyValuePair<FsNode, int>> stack = new Stack<KeyValuePair<FsNode, int>>(2048);
                stack.Push(new KeyValuePair<FsNode, int>(disk.RootNode, 0));

                while (stack.Count > 0)
                {
                    KeyValuePair<FsNode, int> kv = stack.Pop();
                    FsNode node = kv.Key;
                    int depth = kv.Value;

                    if (depth > 0)
                    {
                        string lp = (node.FullPath ?? "").ToLowerInvariant();
                        if (lp.Contains("\\$recycle.bin") || lp.Contains("\\system volume information") ||
                            node.Name.StartsWith("[+"))
                            continue;

                        if (scopeIdx != 3) // Exclude internal C:\Windows system clutter unless 'Todo el Disco (Sin filtro)' is selected
                        {
                            if (lp.StartsWith("c:\\windows\\") || lp == "c:\\windows")
                                continue;
                        }

                        DateTime eff = GetEffectiveDate(node);
                        if (eff.Year > 1985 && node.Size >= minBytes)
                        {
                            bool includeNode = false;
                            if (scopeIdx == 0) // Archivos (Sin Windows interno)
                            {
                                includeNode = !node.IsFolder || node.MatchedInstall != null;
                            }
                            else if (scopeIdx == 1) // Solo Archivos Personales / Sueltos (fuera de carpetas internas de juegos/programas)
                            {
                                if (!node.IsFolder && !lp.Contains("\\program files") && !lp.Contains("\\appdata\\") && !lp.Contains("\\programdata\\"))
                                {
                                    bool insideGame = false;
                                    foreach (string instPath in claimedInstallPaths)
                                    {
                                        if (lp.StartsWith(instPath.ToLowerInvariant() + "\\"))
                                        {
                                            insideGame = true;
                                            break;
                                        }
                                    }
                                    includeNode = !insideGame;
                                }
                            }
                            else if (scopeIdx == 2) // Solo Carpetas Principales e Instalaciones
                            {
                                includeNode = node.IsFolder && (depth <= 2 || node.MatchedInstall != null);
                            }
                            else // Todo el Disco
                            {
                                includeNode = !node.IsFolder || depth <= 2;
                            }

                            if (includeNode && (string.IsNullOrEmpty(search) || MatchesSearch(node, search)))
                            {
                                outList.Add(node);
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
                // Disk hasn't been full-scanned yet: include detected installations on this disk + quick scan of root & user dirs
                if (_allInstallations != null)
                {
                    for (int i = 0; i < _allInstallations.Count; i++)
                    {
                        InstallItem inst = _allInstallations[i];
                        if (!string.Equals(inst.DriveLetter, disk.DriveLetter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (inst.CreatedDate.Year <= 1985 && inst.LastModified.Year <= 1985)
                            continue;
                        if (inst.SizeBytes < minBytes) continue;

                        FsNode vNode = inst.LinkedNode ?? new FsNode
                        {
                            Name = inst.Name,
                            FullPath = inst.InstallLocation,
                            DriveLetter = inst.DriveLetter,
                            Size = inst.SizeBytes,
                            IsFolder = true,
                            CreatedDate = inst.CreatedDate,
                            LastModified = inst.LastModified,
                            Category = inst.Category,
                            MatchedInstall = inst
                        };
                        if (string.IsNullOrEmpty(search) || MatchesSearch(vNode, search))
                        {
                            outList.Add(vNode);
                        }
                    }
                }
            }
        }

        private void BuildChronologicalTreemap()
        {
            Dictionary<string, TreemapItemInput> eraGroups = new Dictionary<string, TreemapItemInput>();
            string[] eraKeys = new string[]
            {
                "ERA_2Y",
                "ERA_1Y",
                "ERA_6M",
                "ERA_1M",
                "ERA_NEW"
            };
            string[] eraTitles = new string[]
            {
                "🕰️ Más de 2 Años de Antigüedad (Muy Antiguos)",
                "📅 Entre 1 y 2 Años de Antigüedad",
                "📆 Entre 6 y 12 Meses de Antigüedad",
                "🗓️ Entre 1 y 6 Meses de Antigüedad",
                "🆕 Últimos 30 Días (Más Nuevos)"
            };
            Color[] eraColors = new Color[]
            {
                Color.FromArgb(220, 38, 38),  // Red/Coral - > 2 years
                Color.FromArgb(234, 88, 12),  // Orange - 1-2 years
                Color.FromArgb(217, 119, 6),  // Amber - 6-12 months
                Color.FromArgb(37, 99, 235),  // Blue - 1-6 months
                Color.FromArgb(16, 185, 129)  // Green - < 30 days
            };

            for (int i = 0; i < eraKeys.Length; i++)
            {
                TreemapItemInput grp = new TreemapItemInput();
                grp.Title = eraTitles[i];
                grp.Color = eraColors[i];
                eraGroups[eraKeys[i]] = grp;
            }

            long totalBytes = 0;
            int limit = Math.Min(400, _displayedChronoNodes.Count);
            for (int i = 0; i < limit; i++)
            {
                FsNode node = _displayedChronoNodes[i];
                if (node.Size <= 0) continue;

                DateTime eff = GetEffectiveDate(node);
                TimeSpan age = DateTime.Now - eff;

                string key;
                Color col;
                if (age.TotalDays >= 730) { key = "ERA_2Y"; col = eraColors[0]; }
                else if (age.TotalDays >= 365) { key = "ERA_1Y"; col = eraColors[1]; }
                else if (age.TotalDays >= 180) { key = "ERA_6M"; col = eraColors[2]; }
                else if (age.TotalDays >= 30) { key = "ERA_1M"; col = eraColors[3]; }
                else { key = "ERA_NEW"; col = eraColors[4]; }

                TreemapItemInput grp = eraGroups[key];
                TreemapItemInput child = new TreemapItemInput();
                child.Title = node.Name;
                child.Badge = string.Format("{0} • {1:dd/MM/yyyy}", node.DriveLetter, eff);
                child.SizeBytes = node.Size;
                child.Node = node;
                child.Install = node.MatchedInstall;
                child.Color = col;

                grp.Children.Add(child);
                grp.SizeBytes += node.Size;
                totalBytes += node.Size;
            }

            List<TreemapItemInput> activeGroups = new List<TreemapItemInput>();
            for (int i = 0; i < eraKeys.Length; i++)
            {
                TreemapItemInput g = eraGroups[eraKeys[i]];
                if (g.Children.Count > 0 && g.SizeBytes > 0)
                {
                    g.Badge = string.Format("{0} archivos", g.Children.Count);
                    activeGroups.Add(g);
                }
            }

            _treemap.EmptyMessage = "Escanea el disco seleccionado ('🔄 Escanear Disco Actual') para ver el mapa de cuadros agrupado del más viejo al más nuevo.";
            _treemap.SetData(activeGroups, totalBytes);
        }

        private void OnLvChronologicalColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_chronoSortCol == e.Column) _chronoSortAsc = !_chronoSortAsc;
            else
            {
                _chronoSortCol = e.Column;
                _chronoSortAsc = (e.Column != 2); // Ascending by default for Date (Oldest first!) and Name
            }
            PopulateChronologicalList();
        }

        private void SortChronoNodesList()
        {
            _displayedChronoNodes.Sort(delegate(FsNode a, FsNode b)
            {
                int cmp = 0;
                switch (_chronoSortCol)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1:
                        cmp = GetEffectiveDate(a).CompareTo(GetEffectiveDate(b));
                        if (cmp == 0) cmp = b.Size.CompareTo(a.Size);
                        break;
                    case 2: cmp = a.Size.CompareTo(b.Size); break;
                    case 3: cmp = string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase); break;
                    case 4: cmp = SafetyAdvisor.AssessNode(a).Level.CompareTo(SafetyAdvisor.AssessNode(b).Level); break;
                    case 5: cmp = a.LastModified.CompareTo(b.LastModified); break;
                    case 6: cmp = a.Category.CompareTo(b.Category); break;
                    case 7: cmp = string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase); break;
                }
                return _chronoSortAsc ? cmp : -cmp;
            });
        }

        private void OnLvChronologicalSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lvChronological.SelectedItems.Count == 0) return;
            FsNode node = _lvChronological.SelectedItems[0].Tag as FsNode;
            if (node != null)
            {
                _lastSelectedNode = node;
                _lastSelectedInstall = node.MatchedInstall;
                _treemap.SelectByNode(node);
                UpdateInspectorFromNode(node);
            }
        }

        private void OnLvChronologicalMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _lvChronological.SelectedItems.Count > 0)
            {
                _contextTarget = _lvChronological.SelectedItems[0].Tag;
                _contextMenu.Show(_lvChronological, e.Location);
            }
        }

        private void PopulateTopFilesList()
        {
            _displayedTopFiles.Clear();
            string search = (_txtSearch.Text ?? "").Trim();

            if (_selectedDisk != null)
            {
                if (_selectedDisk.TopFiles != null)
                {
                    for (int i = 0; i < _selectedDisk.TopFiles.Count; i++)
                    {
                        FsNode f = _selectedDisk.TopFiles[i];
                        if (MatchesSearch(f, search))
                            _displayedTopFiles.Add(f);
                    }
                }
            }
            else
            {
                for (int d = 0; d < _disks.Count; d++)
                {
                    if (_disks[d].TopFiles != null)
                    {
                        for (int i = 0; i < _disks[d].TopFiles.Count; i++)
                        {
                            FsNode f = _disks[d].TopFiles[i];
                            if (MatchesSearch(f, search))
                                _displayedTopFiles.Add(f);
                        }
                    }
                }
            }

            SortTopFilesList();

            _lvTopFiles.BeginUpdate();
            _lvTopFiles.Items.Clear();

            int limit = Math.Min(500, _displayedTopFiles.Count);
            for (int i = 0; i < limit; i++)
            {
                FsNode f = _displayedTopFiles[i];
                ListViewItem lvi = new ListViewItem("📄 " + f.Name);
                lvi.Tag = f;
                SafetyAssessment saTop = SafetyAdvisor.AssessNode(f);
                DateTime dtTop = f.CreatedDate.Year > 1980 ? f.CreatedDate : f.LastModified;

                lvi.SubItems.Add(FormatUtils.FormatBytes(f.Size));
                lvi.SubItems.Add(f.DriveLetter ?? "");
                lvi.SubItems.Add(SafetyAdvisor.FormatShortDate(dtTop));
                lvi.SubItems.Add(saTop.VerdictBadge);
                lvi.SubItems.Add(FormatUtils.GetCategoryLabel(f.Category));
                lvi.SubItems.Add(f.Extension ?? "");
                lvi.SubItems.Add(f.FullPath ?? "");

                if (f.Size >= 2L * 1024L * 1024L * 1024L)
                {
                    lvi.ForeColor = Color.FromArgb(253, 224, 71);
                }
                _lvTopFiles.Items.Add(lvi);
            }
            _lvTopFiles.EndUpdate();

            _tabTopFiles.Text = string.Format("🔥 Top Archivos ({0})", limit);
        }

        private void OnLvFilesColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_filesSortCol == e.Column) _filesSortAsc = !_filesSortAsc;
            else { _filesSortCol = e.Column; _filesSortAsc = (e.Column == 0 || e.Column == 3 || e.Column == 5); }
            PopulateFilesList();
        }

        private void SortFileNodesList()
        {
            _displayedFileNodes.Sort(delegate(FsNode a, FsNode b)
            {
                int cmp = 0;
                switch (_filesSortCol)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1:
                    case 2: cmp = a.Size.CompareTo(b.Size); break;
                    case 3: cmp = a.CreatedDate.CompareTo(b.CreatedDate); break;
                    case 4: cmp = SafetyAdvisor.AssessNode(a).Level.CompareTo(SafetyAdvisor.AssessNode(b).Level); break;
                    case 5: cmp = a.Category.CompareTo(b.Category); break;
                    case 6: cmp = a.FileCount.CompareTo(b.FileCount); break;
                    case 7: cmp = string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase); break;
                }
                return _filesSortAsc ? cmp : -cmp;
            });
        }

        private void OnLvInstallsColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_installsSortCol == e.Column) _installsSortAsc = !_installsSortAsc;
            else { _installsSortCol = e.Column; _installsSortAsc = (e.Column != 1 && e.Column != 3); }
            PopulateInstallationsList();
        }

        private void SortInstallItemsList()
        {
            _displayedInstallItems.Sort(delegate(InstallItem a, InstallItem b)
            {
                int cmp = 0;
                switch (_installsSortCol)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1: cmp = a.SizeBytes.CompareTo(b.SizeBytes); break;
                    case 2: cmp = string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase); break;
                    case 3: cmp = a.CreatedDate.CompareTo(b.CreatedDate); break;
                    case 4: cmp = SafetyAdvisor.AssessInstallation(a).Level.CompareTo(SafetyAdvisor.AssessInstallation(b).Level); break;
                    case 5: cmp = string.Compare(a.SourceType, b.SourceType, StringComparison.OrdinalIgnoreCase); break;
                    case 6: cmp = string.Compare(a.Publisher, b.Publisher, StringComparison.OrdinalIgnoreCase); break;
                    case 7: cmp = string.Compare(a.InstallLocation, b.InstallLocation, StringComparison.OrdinalIgnoreCase); break;
                }
                return _installsSortAsc ? cmp : -cmp;
            });
        }

        private void OnLvTopFilesColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_topFilesSortCol == e.Column) _topFilesSortAsc = !_topFilesSortAsc;
            else { _topFilesSortCol = e.Column; _topFilesSortAsc = (e.Column != 1 && e.Column != 3); }
            PopulateTopFilesList();
        }

        private void SortTopFilesList()
        {
            _displayedTopFiles.Sort(delegate(FsNode a, FsNode b)
            {
                int cmp = 0;
                switch (_topFilesSortCol)
                {
                    case 0: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 1: cmp = a.Size.CompareTo(b.Size); break;
                    case 2: cmp = string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase); break;
                    case 3: cmp = a.CreatedDate.CompareTo(b.CreatedDate); break;
                    case 4: cmp = SafetyAdvisor.AssessNode(a).Level.CompareTo(SafetyAdvisor.AssessNode(b).Level); break;
                    case 5: cmp = a.Category.CompareTo(b.Category); break;
                    case 6: cmp = string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase); break;
                    case 7: cmp = string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase); break;
                }
                return _topFilesSortAsc ? cmp : -cmp;
            });
        }

        // Synchronized selection & drill-down handlers
        private void OnTreemapItemHovered(TreemapRect tr)
        {
            if (tr == null)
            {
                _lblHoverInspector.Text = "💡 Pasa el cursor sobre cualquier cuadro para ver su Fecha Agregado, qué es y si es Fundamental para Windows antes de borrarlo.";
                return;
            }

            if (tr.Install != null)
            {
                SafetyAssessment sa = SafetyAdvisor.AssessInstallation(tr.Install);
                string dtStr = tr.Install.CreatedDate.Year > 1980
                    ? SafetyAdvisor.FormatDateWithRelativeAge(tr.Install.CreatedDate)
                    : (!string.IsNullOrEmpty(tr.Install.InstallDate) ? tr.Install.InstallDate : "N/D");
                _lblHoverInspector.Text = string.Format("💿 {0} ({1})  •  📅 Agregado: {2}  •  {3}  •  ℹ️ {4}",
                    tr.Install.Name,
                    FormatUtils.FormatBytes(tr.SizeBytes),
                    dtStr,
                    sa.VerdictBadge,
                    sa.WhatIsIt);
            }
            else if (tr.Node != null)
            {
                SafetyAssessment sa = SafetyAdvisor.AssessNode(tr.Node);
                DateTime dt = tr.Node.CreatedDate.Year > 1980 ? tr.Node.CreatedDate : tr.Node.LastModified;
                _lblHoverInspector.Text = string.Format("{0} {1} ({2})  •  📅 Agregado: {3}  •  {4}  •  ℹ️ {5}",
                    tr.Node.IsFolder ? "📁" : "📄",
                    tr.Node.Name,
                    FormatUtils.FormatBytes(tr.Node.Size),
                    SafetyAdvisor.FormatDateWithRelativeAge(dt),
                    sa.VerdictBadge,
                    sa.WhatIsIt);
            }
            else
            {
                _lblHoverInspector.Text = string.Format("{0}  •  {1} ({2:0.1}%)",
                    tr.Title, FormatUtils.FormatBytes(tr.SizeBytes), tr.PercentOfDisk);
            }
        }

        private void OnTreemapItemClicked(TreemapRect tr)
        {
            if (tr == null) return;
            _lastSelectedNode = tr.Node;
            _lastSelectedInstall = tr.Install ?? (tr.Node != null ? tr.Node.MatchedInstall : null);

            if (_tabLists.SelectedTab == _tabDuplicates)
            {
                for (int i = 0; i < _lvDuplicates.Items.Count; i++)
                {
                    DuplicateEntry de = _lvDuplicates.Items[i].Tag as DuplicateEntry;
                    if (de != null && ((tr.Install != null && de.Install == tr.Install) || (tr.Node != null && de.Node == tr.Node)))
                    {
                        _lvDuplicates.Items[i].Selected = true;
                        _lvDuplicates.Items[i].EnsureVisible();
                        break;
                    }
                }
            }
            else if (tr.Install != null && _tabLists.SelectedTab == _tabInstalls)
            {
                for (int i = 0; i < _lvInstalls.Items.Count; i++)
                {
                    if (_lvInstalls.Items[i].Tag == tr.Install)
                    {
                        _lvInstalls.Items[i].Selected = true;
                        _lvInstalls.Items[i].EnsureVisible();
                        break;
                    }
                }
            }
            else if (tr.Node != null)
            {
                if (_tabLists.SelectedTab == _tabFiles)
                {
                    for (int i = 0; i < _lvFiles.Items.Count; i++)
                    {
                        FsNode rowNode = _lvFiles.Items[i].Tag as FsNode;
                        if (rowNode == tr.Node || (tr.Node.Parent != null && rowNode == tr.Node.Parent))
                        {
                            _lvFiles.Items[i].Selected = true;
                            _lvFiles.Items[i].EnsureVisible();
                            break;
                        }
                    }
                }
                else if (_tabLists.SelectedTab == _tabChronological)
                {
                    for (int i = 0; i < _lvChronological.Items.Count; i++)
                    {
                        if (_lvChronological.Items[i].Tag == tr.Node)
                        {
                            _lvChronological.Items[i].Selected = true;
                            _lvChronological.Items[i].EnsureVisible();
                            break;
                        }
                    }
                }
                else if (_tabLists.SelectedTab == _tabTopFiles)
                {
                    for (int i = 0; i < _lvTopFiles.Items.Count; i++)
                    {
                        if (_lvTopFiles.Items[i].Tag == tr.Node)
                        {
                            _lvTopFiles.Items[i].Selected = true;
                            _lvTopFiles.Items[i].EnsureVisible();
                            break;
                        }
                    }
                }
            }
        }

        private void OnTreemapItemDoubleClicked(TreemapRect tr)
        {
            if (tr == null) return;

            if (tr.Install != null)
            {
                DrillIntoInstallation(tr.Install);
                return;
            }

            if (tr.Node != null)
            {
                if (tr.Node.IsFolder)
                {
                    for (int i = 0; i < _disks.Count; i++)
                    {
                        if (string.Equals(_disks[i].DriveLetter, tr.Node.DriveLetter, StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedDisk = _disks[i];
                            break;
                        }
                    }
                    _currentFolderNode = tr.Node;
                    if (_treemapMode != TreemapViewMode.HierarchyTwoLevels && _treemapMode != TreemapViewMode.CurrentLevelFlat)
                    {
                        SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
                    }
                    RebuildDiskCardsUI();
                    RefreshCurrentViews(false);
                }
                else
                {
                    OpenPathInExplorer(tr.Node.FullPath, false);
                }
            }
        }

        private void OnTreemapItemRightClicked(TreemapRect tr, Point pt)
        {
            if (tr == null) return;
            _lastSelectedNode = tr.Node;
            _lastSelectedInstall = tr.Install ?? (tr.Node != null ? tr.Node.MatchedInstall : null);
            _contextTarget = (object)tr.Node ?? (object)tr.Install;
            if (_contextTarget != null)
            {
                _contextMenu.Show(_treemap, pt);
            }
        }

        private void OnLvFilesSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lvFiles.SelectedItems.Count == 0) return;
            FsNode node = _lvFiles.SelectedItems[0].Tag as FsNode;
            if (node != null)
            {
                _lastSelectedNode = node;
                _lastSelectedInstall = node.MatchedInstall;
                _treemap.SelectByNode(node);
                UpdateInspectorFromNode(node);
            }
        }

        private void UpdateInspectorFromNode(FsNode node)
        {
            if (node == null) return;
            SafetyAssessment sa = SafetyAdvisor.AssessNode(node);
            DateTime dt = node.CreatedDate.Year > 1980 ? node.CreatedDate : node.LastModified;
            _lblHoverInspector.Text = string.Format("{0} {1} ({2})  •  📅 Agregado: {3}  •  {4}  •  ℹ️ {5}",
                node.IsFolder ? "📁" : "📄",
                node.Name,
                FormatUtils.FormatBytes(node.Size),
                SafetyAdvisor.FormatDateWithRelativeAge(dt),
                sa.VerdictBadge,
                sa.WhatIsIt);
        }

        private void UpdateInspectorFromInstall(InstallItem inst)
        {
            if (inst == null) return;
            SafetyAssessment sa = SafetyAdvisor.AssessInstallation(inst);
            string dtStr = inst.CreatedDate.Year > 1980
                ? SafetyAdvisor.FormatDateWithRelativeAge(inst.CreatedDate)
                : (!string.IsNullOrEmpty(inst.InstallDate) ? inst.InstallDate : "N/D");
            _lblHoverInspector.Text = string.Format("💿 {0} ({1})  •  📅 Agregado: {2}  •  {3}  •  ℹ️ {4}",
                inst.Name,
                FormatUtils.FormatBytes(inst.SizeBytes),
                dtStr,
                sa.VerdictBadge,
                sa.WhatIsIt);
        }

        private void OnLvFilesDoubleClick(object sender, EventArgs e)
        {
            if (_lvFiles.SelectedItems.Count == 0) return;
            FsNode node = _lvFiles.SelectedItems[0].Tag as FsNode;
            if (node == null) return;

            if (node.IsFolder)
            {
                for (int i = 0; i < _disks.Count; i++)
                {
                    if (string.Equals(_disks[i].DriveLetter, node.DriveLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedDisk = _disks[i];
                        break;
                    }
                }
                _currentFolderNode = node;
                RebuildDiskCardsUI();
                RefreshCurrentViews(false);
            }
            else
            {
                OpenPathInExplorer(node.FullPath, false);
            }
        }

        private void OnLvFilesMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _lvFiles.SelectedItems.Count > 0)
            {
                _contextTarget = _lvFiles.SelectedItems[0].Tag;
                _contextMenu.Show(_lvFiles, e.Location);
            }
        }

        private void OnLvInstallsSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lvInstalls.SelectedItems.Count == 0) return;
            InstallItem inst = _lvInstalls.SelectedItems[0].Tag as InstallItem;
            if (inst != null)
            {
                _lastSelectedInstall = inst;
                _lastSelectedNode = inst.LinkedNode;
                _treemap.SelectByInstall(inst);
                UpdateInspectorFromInstall(inst);
            }
        }

        private void OnLvInstallsMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _lvInstalls.SelectedItems.Count > 0)
            {
                _contextTarget = _lvInstalls.SelectedItems[0].Tag;
                _contextMenu.Show(_lvInstalls, e.Location);
            }
        }

        private void OnLvDuplicatesSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lvDuplicates.SelectedItems.Count == 0) return;
            DuplicateEntry de = _lvDuplicates.SelectedItems[0].Tag as DuplicateEntry;
            if (de != null)
            {
                _lastSelectedDuplicate = de;
                _lastSelectedNode = de.Node;
                _lastSelectedInstall = de.Install;
                if (de.Install != null)
                {
                    _treemap.SelectByInstall(de.Install);
                    UpdateInspectorFromInstall(de.Install);
                }
                else if (de.Node != null)
                {
                    _treemap.SelectByNode(de.Node);
                    UpdateInspectorFromNode(de.Node);
                }
            }
        }

        private void OnLvDuplicatesMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _lvDuplicates.SelectedItems.Count > 0)
            {
                DuplicateEntry de = _lvDuplicates.SelectedItems[0].Tag as DuplicateEntry;
                if (de != null)
                {
                    _contextTarget = (object)de.Node ?? (object)de.Install;
                    if (_contextTarget != null)
                        _contextMenu.Show(_lvDuplicates, e.Location);
                }
            }
        }

        private void OnLvTopFilesSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lvTopFiles.SelectedItems.Count == 0) return;
            FsNode node = _lvTopFiles.SelectedItems[0].Tag as FsNode;
            if (node != null)
            {
                _lastSelectedNode = node;
                _lastSelectedInstall = node.MatchedInstall;
                _treemap.SelectByNode(node);
                UpdateInspectorFromNode(node);
            }
        }

        private void OnLvTopFilesMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _lvTopFiles.SelectedItems.Count > 0)
            {
                _contextTarget = _lvTopFiles.SelectedItems[0].Tag;
                _contextMenu.Show(_lvTopFiles, e.Location);
            }
        }

        // =====================================================================
        // DELETE & UNINSTALL ENGINE (WITH ADMINISTRATOR PRIVILEGES)
        // =====================================================================

        private void ResolveCurrentTarget(out FsNode node, out InstallItem inst, out string targetPath, out string displayName, out long sizeBytes)
        {
            node = null;
            inst = null;
            targetPath = "";
            displayName = "";
            sizeBytes = 0;

            if (_tabLists.SelectedTab == _tabDuplicates && _lvDuplicates.SelectedItems.Count > 0)
            {
                DuplicateEntry de = _lvDuplicates.SelectedItems[0].Tag as DuplicateEntry;
                if (de != null)
                {
                    node = de.Node;
                    inst = de.Install;
                    targetPath = de.FullPath;
                    displayName = de.Name;
                    sizeBytes = de.SizeBytes;
                    return;
                }
            }
            else if (_tabLists.SelectedTab == _tabInstalls && _lvInstalls.SelectedItems.Count > 0)
            {
                inst = _lvInstalls.SelectedItems[0].Tag as InstallItem;
            }
            else if (_tabLists.SelectedTab == _tabChronological && _lvChronological.SelectedItems.Count > 0)
            {
                node = _lvChronological.SelectedItems[0].Tag as FsNode;
            }
            else if (_tabLists.SelectedTab == _tabTopFiles && _lvTopFiles.SelectedItems.Count > 0)
            {
                node = _lvTopFiles.SelectedItems[0].Tag as FsNode;
            }
            else if (_tabLists.SelectedTab == _tabFiles && _lvFiles.SelectedItems.Count > 0)
            {
                node = _lvFiles.SelectedItems[0].Tag as FsNode;
            }

            if (node == null && inst == null)
            {
                node = _lastSelectedNode;
                inst = _lastSelectedInstall;
            }

            if (node != null)
            {
                targetPath = node.FullPath;
                displayName = node.Name;
                sizeBytes = node.Size;
                if (inst == null) inst = node.MatchedInstall;
            }
            else if (inst != null)
            {
                targetPath = !string.IsNullOrEmpty(inst.InstallLocation) ? inst.InstallLocation : inst.ExecutableOrIconPath;
                displayName = inst.Name;
                sizeBytes = inst.SizeBytes;
                node = inst.LinkedNode;
            }
        }

        private void DeleteCurrentlySelectedItem(bool permanentAdminDelete)
        {
            FsNode node;
            InstallItem inst;
            string targetPath;
            string displayName;
            long sizeBytes;

            ResolveCurrentTarget(out node, out inst, out targetPath, out displayName, out sizeBytes);

            if (node == null && inst == null && string.IsNullOrEmpty(targetPath))
            {
                MessageBox.Show(
                    "Selecciona primero un cuadro en el Mapa o una fila en cualquiera de las listas (Archivos, Instalaciones o Repetidos) que desees borrar.",
                    "Ningún elemento seleccionado",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ExecuteDeleteOnTarget(node, inst, targetPath, displayName, sizeBytes, permanentAdminDelete);
        }

        private void PromptDeleteOrUninstallSelectedInstall()
        {
            InstallItem inst = null;
            if (_lvInstalls.SelectedItems.Count > 0)
            {
                inst = _lvInstalls.SelectedItems[0].Tag as InstallItem;
            }
            else
            {
                inst = _lastSelectedInstall;
            }

            if (inst == null)
            {
                MessageBox.Show("Selecciona primero un programa o juego de la lista '💿 TODO LO INSTALADO'.",
                    "Seleccionar Instalación", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool hasFolder = !string.IsNullOrEmpty(inst.InstallLocation) && Directory.Exists(inst.InstallLocation);
            bool hasUninstaller = !string.IsNullOrEmpty(inst.UninstallString);

            if (hasFolder && hasUninstaller)
            {
                DialogResult dr = MessageBox.Show(
                    string.Format("Instalación seleccionada: {0}\nTamaño en disco: {1}\nRuta: {2}\n\n" +
                                  "• Haz clic en [SÍ] para BORRAR DIRECTAMENTE la carpeta completa del disco como Administrador (liberación inmediata).\n" +
                                  "• Haz clic en [NO] para ejecutar el Desinstalador Oficial del programa.\n" +
                                  "• Haz clic en [Cancelar] para abortar.",
                        inst.Name,
                        FormatUtils.FormatBytes(inst.SizeBytes),
                        inst.InstallLocation),
                    "🔥 ¿Borrar Carpeta Directamente o Usar Desinstalador?",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    ExecuteDeleteOnTarget(inst.LinkedNode, inst, inst.InstallLocation, inst.Name, inst.SizeBytes, true);
                }
                else if (dr == DialogResult.No)
                {
                    LaunchOfficialUninstaller(inst);
                }
            }
            else if (hasFolder)
            {
                ExecuteDeleteOnTarget(inst.LinkedNode, inst, inst.InstallLocation, inst.Name, inst.SizeBytes, true);
            }
            else if (hasUninstaller)
            {
                LaunchOfficialUninstaller(inst);
            }
            else
            {
                MessageBox.Show(
                    string.Format("El elemento '{0}' no tiene una carpeta física ni un comando de desinstalación activo en el disco.", inst.Name),
                    "No se puede borrar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void LaunchOfficialUninstaller(InstallItem inst)
        {
            if (inst == null || string.IsNullOrEmpty(inst.UninstallString))
            {
                MessageBox.Show("Este elemento no tiene un desinstalador oficial registrado. Puedes borrar su carpeta directamente con '🔥 Borrar Permanente (Admin)'.",
                    "Sin Desinstalador Oficial", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + inst.UninstallString + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                _lblStatusText.Text = "🛠️ Desinstalador oficial iniciado para: " + inst.Name;
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo iniciar el desinstalador: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExecuteDeleteOnTarget(
            FsNode node,
            InstallItem inst,
            string targetPath,
            string displayName,
            long sizeBytes,
            bool permanentAdminDelete)
        {
            if (_isDeleting)
            {
                MessageBox.Show("Ya se está ejecutando una operación de borrado. Espera unos segundos a que termine.",
                    "Borrado en curso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrEmpty(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
            {
                if (inst != null && !string.IsNullOrEmpty(inst.UninstallString))
                {
                    if (MessageBox.Show(
                        string.Format("'{0}' no tiene una carpeta independiente, pero tiene un desinstalador oficial en Windows.\n\n¿Deseas ejecutar su desinstalador oficial ahora?", inst.Name),
                        "Ejecutar Desinstalador",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        LaunchOfficialUninstaller(inst);
                    }
                    return;
                }

                MessageBox.Show("La ruta seleccionada no existe físicamente en el disco:\n" + (targetPath ?? "(Ruta vacía)"),
                    "Ruta no encontrada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (FileDeleter.IsCriticalOperatingSystemRoot(targetPath))
            {
                MessageBox.Show(
                    string.Format("¡PROTECCIÓN DEL SISTEMA!\n\nNo se permite borrar la carpeta raíz crítica del sistema operativo:\n{0}\n\nSi necesitas borrar archivos dentro de ella, haz doble clic para entrar y selecciona la subcarpeta o archivo específico.", targetPath),
                    "Carpeta Raíz Protegida",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            bool isFolder = Directory.Exists(targetPath);
            string kindLabel = inst != null ? "INSTALACIÓN / PROGRAMA" : (isFolder ? "CARPETA COMPLETA" : "ARCHIVO");
            string modeTitle = permanentAdminDelete
                ? "🔥 ELIMINAR PERMANENTEMENTE DEL DISCO (MODO ADMINISTRADOR)"
                : "🗑️ ENVIAR A LA PAPELERA DE RECICLAJE";

            SafetyAssessment sa = inst != null
                ? SafetyAdvisor.AssessInstallation(inst)
                : (node != null ? SafetyAdvisor.AssessNode(node) : SafetyAdvisor.AssessPathAndName(displayName, targetPath, Path.GetExtension(targetPath), isFolder, ItemCategory.OtrosArchivos));

            DateTime dtAdded = DateTime.MinValue;
            if (node != null && node.CreatedDate.Year > 1980) dtAdded = node.CreatedDate;
            else if (inst != null && inst.CreatedDate.Year > 1980) dtAdded = inst.CreatedDate;
            else
            {
                try
                {
                    dtAdded = isFolder ? Directory.GetCreationTime(targetPath) : File.GetCreationTime(targetPath);
                }
                catch { }
            }

            string warningNote = permanentAdminDelete
                ? "⚠️ ATENCIÓN: Esta acción borrará el elemento de forma PERMANENTE usando permisos forzados de Administrador y NO se podrá recuperar."
                : "ℹ️ El elemento se moverá a la Papelera de Reciclaje de Windows.";

            DialogResult confirm = MessageBox.Show(
                string.Format("{0}\n\n" +
                              "• Tipo: {1}\n" +
                              "• Nombre: {2}\n" +
                              "• Espacio a liberar: {3}\n" +
                              "• 📅 Fecha en que fue agregado: {4}\n" +
                              "• 📍 Ruta exacta: {5}\n\n" +
                              "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                              "🛡️ ¿ES FUNDAMENTAL EN LA COMPUTADORA?\n" +
                              "   {6}\n\n" +
                              "ℹ️ ¿Qué es?: {7}\n" +
                              "💡 Consejo: {8}\n" +
                              "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                              "{9}\n\n" +
                              "¿Confirmas que deseas borrarlo ahora?",
                    modeTitle,
                    kindLabel,
                    displayName,
                    FormatUtils.FormatBytes(sizeBytes),
                    SafetyAdvisor.FormatDateWithRelativeAge(dtAdded),
                    targetPath,
                    sa.VerdictBadge,
                    sa.WhatIsIt,
                    sa.DeleteAdvice,
                    warningNote),
                "Confirmar Borrado — " + displayName,
                MessageBoxButtons.YesNo,
                (sa.Level == SafetyLevel.CriticalSystem || permanentAdminDelete) ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
                return;

            _isDeleting = true;
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _btnDeletePermanentBottom.Enabled = false;
            _btnDeleteRecycleBottom.Enabled = false;
            _btnDeleteInstall.Enabled = false;
            _btnDeleteDupItem.Enabled = false;

            Thread delThread = new Thread(delegate()
            {
                string err;
                bool ok;

                if (permanentAdminDelete)
                {
                    ok = FileDeleter.ForceDeletePermanentlyAsAdmin(
                        targetPath,
                        delegate(string progMsg)
                        {
                            SafeInvoke(delegate
                            {
                                _lblStatusText.Text = "🔥 " + progMsg;
                            });
                        },
                        out err);
                }
                else
                {
                    SafeInvoke(delegate
                    {
                        _lblStatusText.Text = "🗑️ Moviendo a la Papelera de Reciclaje: " + targetPath;
                    });
                    ok = FileDeleter.SendToRecycleBin(targetPath, out err);
                }

                SafeInvoke(delegate
                {
                    _isDeleting = false;
                    _progressBar.Visible = false;
                    _btnDeletePermanentBottom.Enabled = true;
                    _btnDeleteRecycleBottom.Enabled = true;
                    _btnDeleteInstall.Enabled = true;
                    _btnDeleteDupItem.Enabled = true;

                    if (ok)
                    {
                        if (_currentFolderNode != null &&
                            string.Equals(FormatUtils.NormalizePath(_currentFolderNode.FullPath), FormatUtils.NormalizePath(targetPath), StringComparison.OrdinalIgnoreCase) &&
                            _currentFolderNode.Parent != null)
                        {
                            _currentFolderNode = _currentFolderNode.Parent;
                        }

                        FileDeleter.RemoveDeletedNodeFromMemory(node, inst, targetPath, _disks, _allInstallations);
                        _lastSelectedNode = null;
                        _lastSelectedInstall = null;
                        _lastSelectedDuplicate = null;

                        PopulateDuplicatesList(true);
                        RebuildDiskCardsUI();
                        RefreshCurrentViews(false);

                        _lblStatusText.Text = string.Format("✔ Eliminado con éxito: {0} ({1} liberados en disco).",
                            displayName,
                            FormatUtils.FormatBytes(sizeBytes));
                    }
                    else
                    {
                        _lblStatusText.Text = "❌ No se pudo completar el borrado de: " + displayName;
                        if (!permanentAdminDelete)
                        {
                            if (MessageBox.Show(
                                string.Format("No se pudo mover a la Papelera de Reciclaje:\n{0}\n\n¿Deseas forzar su ELIMINACIÓN PERMANENTE como Administrador?", err),
                                "Error al enviar a Papelera",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning) == DialogResult.Yes)
                            {
                                ExecuteDeleteOnTarget(node, inst, targetPath, displayName, sizeBytes, true);
                            }
                        }
                        else
                        {
                            MessageBox.Show(
                                "No se pudo eliminar completamente:\n\n" + err,
                                "Error al Eliminar",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        }
                    }
                });
            });
            delThread.IsBackground = true;
            delThread.Start();
        }

        private void DrillIntoSelectedInstallation()
        {
            if (_lvInstalls.SelectedItems.Count == 0)
            {
                MessageBox.Show("Selecciona primero un programa o juego instalado de la lista.",
                    "Seleccionar Instalación", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            InstallItem inst = _lvInstalls.SelectedItems[0].Tag as InstallItem;
            DrillIntoInstallation(inst);
        }

        private void DrillIntoInstallation(InstallItem inst)
        {
            if (inst == null) return;

            if (inst.LinkedNode != null)
            {
                for (int i = 0; i < _disks.Count; i++)
                {
                    if (string.Equals(_disks[i].DriveLetter, inst.DriveLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedDisk = _disks[i];
                        break;
                    }
                }
                _currentFolderNode = inst.LinkedNode;
                SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
                RebuildDiskCardsUI();
                RefreshCurrentViews(false);
                return;
            }

            if (!string.IsNullOrEmpty(inst.InstallLocation) && Directory.Exists(inst.InstallLocation))
            {
                DiskItem tempDisk = new DiskItem();
                tempDisk.DriveLetter = inst.InstallLocation;
                tempDisk.Name = inst.InstallLocation;
                tempDisk.VolumeLabel = inst.Name;
                tempDisk.TotalBytes = Math.Max(inst.SizeBytes, 1);
                tempDisk.UsedBytes = Math.Max(inst.SizeBytes, 1);

                Cursor prevCursor = this.Cursor;
                this.Cursor = Cursors.WaitCursor;
                try
                {
                    NativeScanner.ScanDisk(tempDisk, null, null);
                    if (tempDisk.RootNode != null)
                    {
                        tempDisk.RootNode.Name = inst.Name;
                        tempDisk.RootNode.FullPath = inst.InstallLocation;
                        tempDisk.RootNode.MatchedInstall = inst;
                        inst.LinkedNode = tempDisk.RootNode;

                        for (int i = 0; i < _disks.Count; i++)
                        {
                            if (string.Equals(_disks[i].DriveLetter, inst.DriveLetter, StringComparison.OrdinalIgnoreCase))
                            {
                                _selectedDisk = _disks[i];
                                break;
                            }
                        }

                        _currentFolderNode = tempDisk.RootNode;
                        SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
                        RefreshCurrentViews(false);
                        return;
                    }
                }
                finally
                {
                    this.Cursor = prevCursor;
                }
            }

            if (!string.IsNullOrEmpty(inst.ExecutableOrIconPath) && File.Exists(inst.ExecutableOrIconPath))
            {
                OpenPathInExplorer(inst.ExecutableOrIconPath, false);
                return;
            }

            MessageBox.Show(
                string.Format("La instalación '{0}' está registrada en Windows pero no especifica una carpeta independiente en disco.\n\nOrigen: {1}\nEditor: {2}",
                    inst.Name, inst.SourceType, inst.Publisher),
                "Información de Instalación",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OpenSelectedInstallInExplorer()
        {
            if (_lvInstalls.SelectedItems.Count == 0) return;
            InstallItem inst = _lvInstalls.SelectedItems[0].Tag as InstallItem;
            if (inst == null) return;

            if (!string.IsNullOrEmpty(inst.InstallLocation) && Directory.Exists(inst.InstallLocation))
            {
                OpenPathInExplorer(inst.InstallLocation, true);
            }
            else if (!string.IsNullOrEmpty(inst.ExecutableOrIconPath) && File.Exists(inst.ExecutableOrIconPath))
            {
                OpenPathInExplorer(inst.ExecutableOrIconPath, false);
            }
            else
            {
                MessageBox.Show("Esta instalación del registro no tiene una ruta de carpeta física asociada.",
                    "Ruta no disponible", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OpenSelectedTopFileInExplorer()
        {
            if (_lvTopFiles.SelectedItems.Count == 0) return;
            FsNode node = _lvTopFiles.SelectedItems[0].Tag as FsNode;
            if (node != null && !string.IsNullOrEmpty(node.FullPath))
            {
                OpenPathInExplorer(node.FullPath, false);
            }
        }

        private void OpenCurrentSelectedInExplorer()
        {
            FsNode node;
            InstallItem inst;
            string targetPath;
            string displayName;
            long sizeBytes;

            ResolveCurrentTarget(out node, out inst, out targetPath, out displayName, out sizeBytes);

            if (!string.IsNullOrEmpty(targetPath) && !targetPath.StartsWith("("))
            {
                OpenPathInExplorer(targetPath, Directory.Exists(targetPath));
                return;
            }
            if (_currentFolderNode != null && !string.IsNullOrEmpty(_currentFolderNode.FullPath))
            {
                OpenPathInExplorer(_currentFolderNode.FullPath, true);
            }
        }

        private void ExecuteContextAction(string action)
        {
            FsNode node = _contextTarget as FsNode;
            InstallItem inst = _contextTarget as InstallItem;
            if (node != null && inst == null) inst = node.MatchedInstall;
            if (inst != null && node == null) node = inst.LinkedNode;

            string targetPath = "";
            string displayName = "";
            long sizeBytes = 0;

            if (node != null)
            {
                targetPath = node.FullPath;
                displayName = node.Name;
                sizeBytes = node.Size;
            }
            else if (inst != null)
            {
                targetPath = !string.IsNullOrEmpty(inst.InstallLocation) ? inst.InstallLocation : inst.ExecutableOrIconPath;
                displayName = inst.Name;
                sizeBytes = inst.SizeBytes;
            }

            if (action == "EXPLORER")
            {
                if (!string.IsNullOrEmpty(targetPath))
                    OpenPathInExplorer(targetPath, Directory.Exists(targetPath));
            }
            else if (action == "ZOOM")
            {
                if (inst != null)
                {
                    DrillIntoInstallation(inst);
                }
                else if (node != null && node.IsFolder)
                {
                    _currentFolderNode = node;
                    if (_treemapMode != TreemapViewMode.HierarchyTwoLevels && _treemapMode != TreemapViewMode.CurrentLevelFlat)
                        SetTreemapMode(TreemapViewMode.HierarchyTwoLevels, true);
                    RefreshCurrentViews(false);
                }
            }
            else if (action == "COPY")
            {
                if (!string.IsNullOrEmpty(targetPath))
                {
                    try { Clipboard.SetText(targetPath); } catch { }
                }
            }
            else if (action == "DELETE_RECYCLE")
            {
                ExecuteDeleteOnTarget(node, inst, targetPath, displayName, sizeBytes, false);
            }
            else if (action == "DELETE_PERMANENT")
            {
                ExecuteDeleteOnTarget(node, inst, targetPath, displayName, sizeBytes, true);
            }
            else if (action == "UNINSTALL")
            {
                if (inst != null)
                {
                    LaunchOfficialUninstaller(inst);
                }
                else
                {
                    MessageBox.Show("Este elemento es un archivo o carpeta estándar, no una instalación con desinstalador en el registro. Usa '🔥 Eliminar Permanentemente' o '🗑️ Enviar a la Papelera' para borrarlo.",
                        "Sin Desinstalador", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OpenPathInExplorer(string path, bool isFolder)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (isFolder && Directory.Exists(path))
                {
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
                else if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { }
        }

        private void ExportInteractiveReport()
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Exportar Reporte Completo de Discos, Instalaciones y Repetidos";
                sfd.Filter = "Reporte Web HTML (*.html)|*.html|Archivo CSV para Excel (*.csv)|*.csv";
                sfd.FileName = string.Format("Reporte_Discos_Instalaciones_Repetidos_{0:yyyyMMdd_HHmm}.html", DateTime.Now);

                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    if (sfd.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        ExportToCsv(sfd.FileName);
                    }
                    else
                    {
                        ExportToHtml(sfd.FileName);
                    }

                    if (MessageBox.Show("Reporte exportado correctamente en:\n" + sfd.FileName + "\n\n¿Deseas abrirlo ahora?",
                        "Reporte Exportado", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        Process.Start(sfd.FileName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al exportar el reporte: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportToCsv(string filePath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Seccion,GrupoORelacion,Nombre,TamanoBytes,TamanoFormateado,Disco,TipoOrigen,VarianteVersion,Ruta");

            for (int i = 0; i < _disks.Count; i++)
            {
                DiskItem d = _disks[i];
                sb.AppendFormat("DISCO,\"\",\"{0}\",{1},\"{2}\",\"{3}\",\"{4}\",\"\",\"{5}\"\r\n",
                    EscapeCsv(d.DisplayTitle), d.UsedBytes, FormatUtils.FormatBytes(d.UsedBytes), d.DriveLetter, d.DriveFormat, d.Name);
            }

            for (int g = 0; g < _allDuplicateGroups.Count; g++)
            {
                DuplicateGroup grp = _allDuplicateGroups[g];
                for (int e = 0; e < grp.Entries.Count; e++)
                {
                    DuplicateEntry de = grp.Entries[e];
                    sb.AppendFormat("REPETIDO_SIMILAR,\"{0}\",\"{1}\",{2},\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\"\r\n",
                        EscapeCsv(grp.DisplayGroupTitle), EscapeCsv(de.Name), de.SizeBytes, FormatUtils.FormatBytes(de.SizeBytes),
                        EscapeCsv(de.DriveLetter), EscapeCsv(grp.KindLabel), EscapeCsv(de.VersionOrVariant), EscapeCsv(de.FullPath));
                }
            }

            for (int i = 0; i < _allInstallations.Count; i++)
            {
                InstallItem inst = _allInstallations[i];
                sb.AppendFormat("INSTALACION,\"\",\"{0}\",{1},\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\"\r\n",
                    EscapeCsv(inst.Name), inst.SizeBytes, FormatUtils.FormatBytes(inst.SizeBytes),
                    EscapeCsv(inst.DriveLetter), EscapeCsv(inst.SourceType), EscapeCsv(inst.Version),
                    EscapeCsv(inst.InstallLocation));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\"", "\"\"");
        }

        private void ExportToHtml(string filePath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang='es'><head><meta charset='UTF-8'>");
            sb.AppendLine("<title>Reporte de Discos, Instalaciones y Repetidos</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{background:#0f172a;color:#f1f5f9;font-family:'Segoe UI',sans-serif;margin:24px;}");
            sb.AppendLine("h1,h2{color:#38bdf8;} .cards{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px;}");
            sb.AppendLine(".card{background:#1e293b;border:1px solid #334155;border-radius:8px;padding:16px;min-width:240px;}");
            sb.AppendLine("table{width:100%;border-collapse:collapse;background:#1e293b;margin-bottom:32px;font-size:13px;}");
            sb.AppendLine("th,td{padding:8px 12px;border-bottom:1px solid #334155;text-align:left;}");
            sb.AppendLine("th{background:#0f172a;color:#93c5fd;} tr:hover{background:#334155;}");
            sb.AppendLine(".right{text-align:right;font-weight:bold;color:#fde047;}");
            sb.AppendLine("</style></head><body>");
            sb.AppendFormat("<h1>Reporte de Discos, Instalaciones y Repetidos ({0:yyyy-MM-dd HH:mm})</h1>", DateTime.Now);

            sb.AppendLine("<h2>1. Discos Detectados</h2><div class='cards'>");
            for (int i = 0; i < _disks.Count; i++)
            {
                DiskItem d = _disks[i];
                sb.AppendFormat("<div class='card'><h3>{0}</h3><p>Usado: <b>{1}</b> / {2} ({3:0.1}%)</p><p>Libre: {4}</p></div>",
                    System.Net.WebUtility.HtmlEncode(d.DisplayTitle),
                    FormatUtils.FormatBytes(d.UsedBytes),
                    FormatUtils.FormatBytes(d.TotalBytes),
                    d.UsedPercent,
                    FormatUtils.FormatBytes(d.FreeBytes));
            }
            sb.AppendLine("</div>");

            sb.AppendFormat("<h2>2. Instalaciones y Archivos Repetidos o de Distinta Versión ({0} grupos)</h2>", _allDuplicateGroups.Count);
            sb.AppendLine("<table><thead><tr><th>Grupo</th><th>Nombre</th><th class='right'>Tamaño</th><th class='right'>Ahorro Grupo</th><th>Disco</th><th>Tipo</th><th>Versión / Variante</th><th>Ruta</th></tr></thead><tbody>");
            for (int g = 0; g < _allDuplicateGroups.Count; g++)
            {
                DuplicateGroup grp = _allDuplicateGroups[g];
                for (int e = 0; e < grp.Entries.Count; e++)
                {
                    DuplicateEntry de = grp.Entries[e];
                    sb.AppendFormat("<tr><td><b>{0}</b></td><td>{1}</td><td class='right'>{2}</td><td class='right'>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td><td>{7}</td></tr>",
                        System.Net.WebUtility.HtmlEncode(grp.DisplayGroupTitle),
                        System.Net.WebUtility.HtmlEncode(de.Name),
                        FormatUtils.FormatBytes(de.SizeBytes),
                        e == 0 ? ("+" + FormatUtils.FormatBytes(grp.PotentialSavingsBytes)) : "",
                        System.Net.WebUtility.HtmlEncode(de.DriveLetter),
                        System.Net.WebUtility.HtmlEncode(grp.KindLabel),
                        System.Net.WebUtility.HtmlEncode(de.VersionOrVariant),
                        System.Net.WebUtility.HtmlEncode(de.FullPath));
                }
            }
            sb.AppendLine("</tbody></table>");

            sb.AppendFormat("<h2>3. Todo lo Instalado ({0} instalaciones detectadas)</h2>", _allInstallations.Count);
            sb.AppendLine("<table><thead><tr><th>Programa / Instalación</th><th class='right'>Tamaño Real</th><th>Disco</th><th>Origen</th><th>Editor</th><th>Ruta</th></tr></thead><tbody>");
            for (int i = 0; i < _allInstallations.Count; i++)
            {
                InstallItem inst = _allInstallations[i];
                sb.AppendFormat("<tr><td>{0}</td><td class='right'>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td></tr>",
                    System.Net.WebUtility.HtmlEncode(inst.Name),
                    FormatUtils.FormatBytes(inst.SizeBytes),
                    System.Net.WebUtility.HtmlEncode(inst.DriveLetter),
                    System.Net.WebUtility.HtmlEncode(inst.SourceType),
                    System.Net.WebUtility.HtmlEncode(inst.Publisher),
                    System.Net.WebUtility.HtmlEncode(inst.InstallLocation));
            }
            sb.AppendLine("</tbody></table></body></html>");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private void SafeInvoke(Action action)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                    this.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }
    }
}
