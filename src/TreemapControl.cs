using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AnalizadorDiscosPortable
{
    public enum TreemapViewMode
    {
        HierarchyTwoLevels = 0,
        CurrentLevelFlat = 1,
        InstallationsMap = 2,
        TopHeavyFilesMap = 3,
        DuplicatesAndSimilarsMap = 4,
        ChronologicalAgeMap = 5
    }

    public class TreemapItemInput
    {
        public string Title;
        public string Subtitle;
        public string Badge;
        public long SizeBytes;
        public Color Color;
        public FsNode Node;
        public InstallItem Install;
        public List<TreemapItemInput> Children;

        public TreemapItemInput()
        {
            Children = new List<TreemapItemInput>();
        }
    }

    public class TreemapControl : Control
    {
        private List<TreemapRect> _rootRects = new List<TreemapRect>();
        private List<TreemapItemInput> _currentInputs = new List<TreemapItemInput>();
        private long _totalDiskBytes = 1;
        private long _totalViewBytes = 1;
        private TreemapRect _hoveredRect = null;
        private TreemapRect _selectedRect = null;
        private Point _mousePos = Point.Empty;
        private string _emptyMessage = "Selecciona un disco o haz clic en 'Escanear' para visualizar el mapa de cuadros.";

        private Font _fontGroupTitle;
        private Font _fontBoxTitleLarge;
        private Font _fontBoxTitleMedium;
        private Font _fontBoxTitleSmall;
        private Font _fontBoxSub;
        private Font _fontBadge;
        private Font _fontTooltipTitle;
        private Font _fontTooltipBody;

        public event Action<TreemapRect> ItemClicked;
        public event Action<TreemapRect> ItemDoubleClicked;
        public event Action<TreemapRect, Point> ItemRightClicked;
        public event Action<TreemapRect> ItemHovered;

        public TreemapControl()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);

            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.ForeColor = Color.White;

            _fontGroupTitle = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _fontBoxTitleLarge = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            _fontBoxTitleMedium = new Font("Segoe UI", 8.75f, FontStyle.Bold);
            _fontBoxTitleSmall = new Font("Segoe UI", 7.5f, FontStyle.Regular);
            _fontBoxSub = new Font("Segoe UI", 8.0f, FontStyle.Regular);
            _fontBadge = new Font("Segoe UI", 7.0f, FontStyle.Bold);
            _fontTooltipTitle = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _fontTooltipBody = new Font("Segoe UI", 8.25f, FontStyle.Regular);
        }

        public string EmptyMessage
        {
            get { return _emptyMessage; }
            set { _emptyMessage = value; this.Invalidate(); }
        }

        public void SetData(List<TreemapItemInput> items, long totalDiskBytes)
        {
            _currentInputs = items ?? new List<TreemapItemInput>();
            _totalViewBytes = 0;
            for (int i = 0; i < _currentInputs.Count; i++)
            {
                if (_currentInputs[i].SizeBytes > 0)
                    _totalViewBytes += _currentInputs[i].SizeBytes;
            }
            if (_totalViewBytes <= 0) _totalViewBytes = 1;
            _totalDiskBytes = totalDiskBytes > 0 ? totalDiskBytes : _totalViewBytes;

            _hoveredRect = null;
            _selectedRect = null;
            RebuildLayout();
            this.Invalidate();
        }

        public void SelectByNode(FsNode node)
        {
            if (node == null)
            {
                _selectedRect = null;
                this.Invalidate();
                return;
            }
            _selectedRect = FindRectByNode(_rootRects, node);
            this.Invalidate();
        }

        public void SelectByInstall(InstallItem inst)
        {
            if (inst == null)
            {
                _selectedRect = null;
                this.Invalidate();
                return;
            }
            _selectedRect = FindRectByInstall(_rootRects, inst);
            this.Invalidate();
        }

        private TreemapRect FindRectByNode(List<TreemapRect> list, FsNode node)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                TreemapRect r = list[i];
                if (r.Node == node) return r;
                if (r.Children != null && r.Children.Count > 0)
                {
                    TreemapRect found = FindRectByNode(r.Children, node);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private TreemapRect FindRectByInstall(List<TreemapRect> list, InstallItem inst)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                TreemapRect r = list[i];
                if (r.Install == inst || (r.Node != null && r.Node.MatchedInstall == inst))
                    return r;
                if (r.Children != null && r.Children.Count > 0)
                {
                    TreemapRect found = FindRectByInstall(r.Children, inst);
                    if (found != null) return found;
                }
            }
            return null;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildLayout();
            this.Invalidate();
        }

        private void RebuildLayout()
        {
            _rootRects.Clear();
            if (_currentInputs == null || _currentInputs.Count == 0) return;

            RectangleF canvas = new RectangleF(4, 4, Math.Max(10, this.ClientSize.Width - 8), Math.Max(10, this.ClientSize.Height - 8));
            _rootRects = BuildSquarifiedTreemap(_currentInputs, canvas, _totalViewBytes, _totalDiskBytes, 0);
        }

        private List<TreemapRect> BuildSquarifiedTreemap(
            List<TreemapItemInput> rawInputs,
            RectangleF bounds,
            long parentBytes,
            long diskBytes,
            int depth)
        {
            List<TreemapRect> result = new List<TreemapRect>();
            if (rawInputs == null || rawInputs.Count == 0 || bounds.Width < 4 || bounds.Height < 4)
                return result;

            List<TreemapItemInput> sorted = new List<TreemapItemInput>(rawInputs.Count);
            for (int i = 0; i < rawInputs.Count; i++)
            {
                if (rawInputs[i].SizeBytes > 0)
                    sorted.Add(rawInputs[i]);
            }
            if (sorted.Count == 0) return result;

            sorted.Sort(delegate(TreemapItemInput a, TreemapItemInput b)
            {
                return b.SizeBytes.CompareTo(a.SizeBytes);
            });

            // Cap maximum items per container to keep boxes readable and fast
            int maxItems = depth == 0 ? 75 : 28;
            if (sorted.Count > maxItems)
            {
                long extraBytes = 0;
                int extraCount = 0;
                for (int i = maxItems - 1; i < sorted.Count; i++)
                {
                    extraBytes += sorted[i].SizeBytes;
                    extraCount++;
                }
                sorted.RemoveRange(maxItems - 1, sorted.Count - (maxItems - 1));
                if (extraBytes > 0)
                {
                    TreemapItemInput grouped = new TreemapItemInput();
                    grouped.Title = string.Format("[+{0} elementos menores]", extraCount);
                    grouped.Subtitle = FormatUtils.FormatBytes(extraBytes);
                    grouped.SizeBytes = extraBytes;
                    grouped.Color = Color.FromArgb(71, 85, 105);
                    sorted.Add(grouped);
                }
            }

            double totalWeight = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                totalWeight += (double)sorted[i].SizeBytes;
            }
            if (totalWeight <= 0) return result;

            double totalArea = (double)bounds.Width * (double)bounds.Height;
            double[] areas = new double[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                areas[i] = ((double)sorted[i].SizeBytes / totalWeight) * totalArea;
            }

            RectangleF remaining = bounds;
            int idx = 0;
            while (idx < sorted.Count && remaining.Width >= 2f && remaining.Height >= 2f)
            {
                List<int> rowIndices = new List<int>();
                rowIndices.Add(idx);
                double rowAreaSum = areas[idx];
                double shortSide = Math.Min(remaining.Width, remaining.Height);
                double currentWorst = WorstAspectRatio(areas, rowIndices, rowAreaSum, shortSide);

                int next = idx + 1;
                while (next < sorted.Count)
                {
                    rowIndices.Add(next);
                    double candidateSum = rowAreaSum + areas[next];
                    double candidateWorst = WorstAspectRatio(areas, rowIndices, candidateSum, shortSide);
                    if (candidateWorst <= currentWorst)
                    {
                        currentWorst = candidateWorst;
                        rowAreaSum = candidateSum;
                        next++;
                    }
                    else
                    {
                        rowIndices.RemoveAt(rowIndices.Count - 1);
                        break;
                    }
                }

                // Lay out current row along the shorter side
                bool horizontalSplit = remaining.Width >= remaining.Height;
                if (horizontalSplit)
                {
                    // Row goes vertically on the left side of 'remaining'
                    float rowWidth = remaining.Height > 0 ? (float)(rowAreaSum / remaining.Height) : 0f;
                    if (rowWidth > remaining.Width || next >= sorted.Count) rowWidth = remaining.Width;

                    float currY = remaining.Y;
                    for (int r = 0; r < rowIndices.Count; r++)
                    {
                        int itemIdx = rowIndices[r];
                        float itemHeight = (r == rowIndices.Count - 1)
                            ? (remaining.Bottom - currY)
                            : (rowAreaSum > 0 ? (float)((areas[itemIdx] / rowAreaSum) * remaining.Height) : 0f);

                        RectangleF itemRect = new RectangleF(remaining.X, currY, rowWidth, Math.Max(0f, itemHeight));
                        result.Add(CreateTreemapRect(sorted[itemIdx], itemRect, parentBytes, diskBytes, depth));
                        currY += itemHeight;
                    }

                    remaining = new RectangleF(remaining.X + rowWidth, remaining.Y, Math.Max(0f, remaining.Width - rowWidth), remaining.Height);
                }
                else
                {
                    // Row goes horizontally across the top of 'remaining'
                    float rowHeight = remaining.Width > 0 ? (float)(rowAreaSum / remaining.Width) : 0f;
                    if (rowHeight > remaining.Height || next >= sorted.Count) rowHeight = remaining.Height;

                    float currX = remaining.X;
                    for (int r = 0; r < rowIndices.Count; r++)
                    {
                        int itemIdx = rowIndices[r];
                        float itemWidth = (r == rowIndices.Count - 1)
                            ? (remaining.Right - currX)
                            : (rowAreaSum > 0 ? (float)((areas[itemIdx] / rowAreaSum) * remaining.Width) : 0f);

                        RectangleF itemRect = new RectangleF(currX, remaining.Y, Math.Max(0f, itemWidth), rowHeight);
                        result.Add(CreateTreemapRect(sorted[itemIdx], itemRect, parentBytes, diskBytes, depth));
                        currX += itemWidth;
                    }

                    remaining = new RectangleF(remaining.X, remaining.Y + rowHeight, remaining.Width, Math.Max(0f, remaining.Height - rowHeight));
                }

                idx = next;
            }

            return result;
        }

        private static double WorstAspectRatio(double[] areas, List<int> rowIndices, double rowAreaSum, double shortSide)
        {
            if (rowIndices.Count == 0 || rowAreaSum <= 0 || shortSide <= 0) return double.MaxValue;
            double s2 = rowAreaSum * rowAreaSum;
            double w2 = shortSide * shortSide;
            double worst = 0.0;
            for (int i = 0; i < rowIndices.Count; i++)
            {
                double r = areas[rowIndices[i]];
                if (r <= 0) continue;
                double ratio1 = (w2 * r) / s2;
                double ratio2 = s2 / (w2 * r);
                double maxRatio = Math.Max(ratio1, ratio2);
                if (maxRatio > worst) worst = maxRatio;
            }
            return worst;
        }

        private TreemapRect CreateTreemapRect(
            TreemapItemInput input,
            RectangleF rect,
            long parentBytes,
            long diskBytes,
            int depth)
        {
            TreemapRect tr = new TreemapRect();
            tr.Bounds = rect;
            tr.Title = input.Title ?? "";
            tr.Subtitle = input.Subtitle ?? "";
            tr.Badge = input.Badge ?? "";
            tr.SizeBytes = input.SizeBytes;
            tr.PercentOfParent = parentBytes > 0 ? ((double)input.SizeBytes * 100.0 / (double)parentBytes) : 0.0;
            tr.PercentOfDisk = diskBytes > 0 ? ((double)input.SizeBytes * 100.0 / (double)diskBytes) : 0.0;
            tr.BaseColor = input.Color;
            tr.Node = input.Node;
            tr.Install = input.Install;
            tr.Depth = depth;

            bool hasChildren = input.Children != null && input.Children.Count > 0 && rect.Width >= 68f && rect.Height >= 52f;
            tr.IsGroup = hasChildren;

            if (hasChildren)
            {
                float headerH = Math.Min(21f, Math.Max(16f, rect.Height * 0.16f));
                tr.HeaderBounds = new RectangleF(rect.X + 2f, rect.Y + 2f, Math.Max(0f, rect.Width - 4f), headerH);
                tr.ContentBounds = new RectangleF(
                    rect.X + 3f,
                    rect.Y + headerH + 3f,
                    Math.Max(0f, rect.Width - 6f),
                    Math.Max(0f, rect.Height - headerH - 6f));

                if (tr.ContentBounds.Width >= 16f && tr.ContentBounds.Height >= 16f)
                {
                    tr.Children = BuildSquarifiedTreemap(
                        input.Children,
                        tr.ContentBounds,
                        input.SizeBytes,
                        diskBytes,
                        depth + 1);
                }
            }
            else
            {
                tr.HeaderBounds = rect;
                tr.ContentBounds = rect;
            }

            return tr;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            g.Clear(this.BackColor);

            if (_rootRects == null || _rootRects.Count == 0)
            {
                using (SolidBrush br = new SolidBrush(Color.FromArgb(148, 163, 184)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(_emptyMessage, _fontBoxTitleLarge, br, this.ClientRectangle, sf);
                }
                return;
            }

            for (int i = 0; i < _rootRects.Count; i++)
            {
                DrawTreemapRect(g, _rootRects[i]);
            }

            // Highlight selected rect
            if (_selectedRect != null)
            {
                Rectangle rSel = Rectangle.Round(_selectedRect.Bounds);
                if (rSel.Width > 2 && rSel.Height > 2)
                {
                    using (Pen pSel = new Pen(Color.FromArgb(250, 204, 21), 2.5f)) // Bright yellow gold border
                    {
                        g.DrawRectangle(pSel, rSel.X + 1, rSel.Y + 1, Math.Max(1, rSel.Width - 2), Math.Max(1, rSel.Height - 2));
                    }
                }
            }

            // Highlight hovered rect & draw floating tooltip
            if (_hoveredRect != null)
            {
                Rectangle rHov = Rectangle.Round(_hoveredRect.Bounds);
                if (rHov.Width > 1 && rHov.Height > 1)
                {
                    using (Pen pHov = new Pen(Color.White, 2f))
                    {
                        g.DrawRectangle(pHov, rHov.X + 1, rHov.Y + 1, Math.Max(1, rHov.Width - 2), Math.Max(1, rHov.Height - 2));
                    }
                }
                DrawFloatingTooltip(g, _hoveredRect, _mousePos);
            }
        }

        private void DrawTreemapRect(Graphics g, TreemapRect tr)
        {
            Rectangle r = Rectangle.Round(tr.Bounds);
            if (r.Width <= 1 || r.Height <= 1) return;

            // Inset by 1px for crisp grid gap
            Rectangle box = new Rectangle(r.X + 1, r.Y + 1, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));

            if (tr.IsGroup && tr.Children != null && tr.Children.Count > 0)
            {
                Color groupBg = DarkenColor(tr.BaseColor, 0.55f);
                Color headerBg = DarkenColor(tr.BaseColor, 0.30f);
                Color borderCol = LightenColor(tr.BaseColor, 0.25f);

                using (SolidBrush bBg = new SolidBrush(groupBg))
                {
                    g.FillRectangle(bBg, box);
                }

                Rectangle hdr = Rectangle.Round(tr.HeaderBounds);
                if (hdr.Width > 2 && hdr.Height > 2)
                {
                    using (SolidBrush bHdr = new SolidBrush(headerBg))
                    {
                        g.FillRectangle(bHdr, hdr);
                    }

                    if (hdr.Width >= 45 && hdr.Height >= 13)
                    {
                        string hdrText = string.Format("{0}  ({1} • {2:0.0}%)",
                            tr.Title,
                            FormatUtils.FormatBytes(tr.SizeBytes),
                            tr.PercentOfDisk);

                        if (!string.IsNullOrEmpty(tr.Badge))
                        {
                            hdrText = "[" + tr.Badge + "] " + hdrText;
                        }

                        Rectangle textRect = new Rectangle(hdr.X + 4, hdr.Y + 1, Math.Max(1, hdr.Width - 8), Math.Max(1, hdr.Height - 2));
                        using (SolidBrush bText = new SolidBrush(Color.FromArgb(248, 250, 252)))
                        using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                        {
                            sf.Trimming = StringTrimming.EllipsisCharacter;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(hdrText, _fontGroupTitle, bText, textRect, sf);
                        }
                    }
                }

                for (int i = 0; i < tr.Children.Count; i++)
                {
                    DrawTreemapRect(g, tr.Children[i]);
                }

                using (Pen pBorder = new Pen(borderCol, 1f))
                {
                    g.DrawRectangle(pBorder, box.X, box.Y, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1));
                }
            }
            else
            {
                bool isHovered = (tr == _hoveredRect);
                Color cTop = isHovered ? LightenColor(tr.BaseColor, 0.22f) : LightenColor(tr.BaseColor, 0.08f);
                Color cBot = isHovered ? DarkenColor(tr.BaseColor, 0.12f) : DarkenColor(tr.BaseColor, 0.28f);

                if (box.Width >= 8 && box.Height >= 8)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(box, cTop, cBot, LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(lgb, box);
                    }
                }
                else
                {
                    using (SolidBrush sb = new SolidBrush(tr.BaseColor))
                    {
                        g.FillRectangle(sb, box);
                    }
                }

                // Inner subtle highlight border
                if (box.Width >= 4 && box.Height >= 4)
                {
                    Color borderCol = isHovered
                        ? Color.White
                        : Color.FromArgb(110, 255, 255, 255);
                    using (Pen p = new Pen(borderCol, 1f))
                    {
                        g.DrawRectangle(p, box.X, box.Y, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1));
                    }
                }

                DrawLeafLabels(g, tr, box);
            }
        }

        private void DrawLeafLabels(Graphics g, TreemapRect tr, Rectangle box)
        {
            if (box.Width < 28 || box.Height < 16) return;

            int padX = 5;
            int padY = 4;
            int availW = box.Width - padX * 2;
            int availH = box.Height - padY * 2;
            if (availW <= 8 || availH <= 8) return;

            int curY = box.Y + padY;

            // Optional Badge pill if box is big enough
            if (!string.IsNullOrEmpty(tr.Badge) && box.Width >= 85 && box.Height >= 52)
            {
                string badgeText = tr.Badge;
                SizeF bSize = g.MeasureString(badgeText, _fontBadge);
                int bw = Math.Min(availW, (int)bSize.Width + 10);
                int bh = 15;
                Rectangle badgeRect = new Rectangle(box.X + padX, curY, bw, bh);

                using (SolidBrush bPill = new SolidBrush(Color.FromArgb(140, 15, 23, 42)))
                {
                    g.FillRectangle(bPill, badgeRect);
                }
                using (SolidBrush bTxt = new SolidBrush(Color.FromArgb(253, 224, 71)))
                using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(badgeText, _fontBadge, bTxt, badgeRect, sf);
                }
                curY += bh + 3;
                availH -= (bh + 3);
            }

            string sizeStr = FormatUtils.FormatBytes(tr.SizeBytes);
            string pctStr = string.Format("{0:0.1}%", tr.PercentOfDisk);

            Font titleFont = (box.Width >= 140 && box.Height >= 75)
                ? _fontBoxTitleLarge
                : ((box.Width >= 70 && box.Height >= 36) ? _fontBoxTitleMedium : _fontBoxTitleSmall);

            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoClip;

                if (availH >= 34 && box.Width >= 60)
                {
                    // Draw Title + Size/Percentage line below
                    int titleH = (availH >= 52 && box.Width >= 110) ? 34 : 18;
                    Rectangle titleRect = new Rectangle(box.X + padX, curY, availW, titleH);

                    // Drop shadow for high contrast
                    Rectangle shadowRect = new Rectangle(titleRect.X + 1, titleRect.Y + 1, titleRect.Width, titleRect.Height);
                    using (SolidBrush bShadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    {
                        g.DrawString(tr.Title, titleFont, bShadow, shadowRect, sf);
                    }
                    using (SolidBrush bTitle = new SolidBrush(Color.White))
                    {
                        g.DrawString(tr.Title, titleFont, bTitle, titleRect, sf);
                    }

                    int subY = curY + (titleH == 34 ? 32 : 18);
                    if (subY + 14 <= box.Bottom - 2)
                    {
                        string subLine = box.Width >= 95
                            ? string.Format("{0}  ({1})", sizeStr, pctStr)
                            : sizeStr;

                        Rectangle subRect = new Rectangle(box.X + padX, subY, availW, 16);
                        using (SolidBrush bSub = new SolidBrush(Color.FromArgb(230, 241, 245, 249)))
                        using (StringFormat sfNoWrap = new StringFormat(StringFormatFlags.NoWrap))
                        {
                            sfNoWrap.Trimming = StringTrimming.EllipsisCharacter;
                            g.DrawString(subLine, _fontBoxSub, bSub, subRect, sfNoWrap);
                        }
                    }
                }
                else
                {
                    // Single compact line
                    Rectangle singleRect = new Rectangle(box.X + 3, box.Y + 2, box.Width - 6, box.Height - 4);
                    using (StringFormat sfSingle = new StringFormat(StringFormatFlags.NoWrap))
                    {
                        sfSingle.Trimming = StringTrimming.EllipsisCharacter;
                        sfSingle.LineAlignment = StringAlignment.Center;
                        string compact = box.Width >= 90 ? (tr.Title + " (" + sizeStr + ")") : tr.Title;
                        using (SolidBrush bTitle = new SolidBrush(Color.White))
                        {
                            g.DrawString(compact, _fontBoxTitleSmall, bTitle, singleRect, sfSingle);
                        }
                    }
                }
            }
        }

        private void DrawFloatingTooltip(Graphics g, TreemapRect tr, Point mouse)
        {
            if (tr == null) return;

            List<string> lines = new List<string>();
            string title = tr.Title;
            SafetyAssessment safety = null;

            lines.Add(string.Format("⚖️ Peso en disco: {0}  ({1:0.2}% del disco)", FormatUtils.FormatBytes(tr.SizeBytes), tr.PercentOfDisk));

            if (tr.Install != null)
            {
                safety = SafetyAdvisor.AssessInstallation(tr.Install);
                DateTime created = tr.Install.CreatedDate;
                DateTime modified = tr.Install.LastModified;
                if (created.Year <= 1980 && tr.Node != null) created = tr.Node.CreatedDate;
                if (modified.Year <= 1980 && tr.Node != null) modified = tr.Node.LastModified;

                if (created.Year > 1980)
                    lines.Add("📅 Fecha agregado / instalado: " + SafetyAdvisor.FormatDateWithRelativeAge(created));
                else if (!string.IsNullOrEmpty(tr.Install.InstallDate))
                    lines.Add("📅 Fecha de instalación: " + tr.Install.InstallDate);
                else
                    lines.Add("📅 Fecha agregado: Registrado en sistema");

                if (modified.Year > 1980)
                    lines.Add("🕒 Última modificación: " + SafetyAdvisor.FormatDateWithRelativeAge(modified));

                lines.Add("📦 Origen: " + (tr.Install.SourceType ?? "Instalación") +
                    (string.IsNullOrEmpty(tr.Install.Publisher) ? "" : (" • Editor: " + tr.Install.Publisher)) +
                    (string.IsNullOrEmpty(tr.Install.Version) ? "" : (" • v" + tr.Install.Version)));

                if (safety != null)
                {
                    lines.Add("SAFETY_BADGE:" + safety.VerdictBadge);
                    lines.Add("ℹ️ ¿Qué es?: " + safety.WhatIsIt);
                    lines.Add("🛡️ Consejo: " + safety.DeleteAdvice);
                }

                if (!string.IsNullOrEmpty(tr.Install.InstallLocation))
                    lines.Add("📍 Ruta: " + tr.Install.InstallLocation);

                lines.Add("💡 Doble clic: Ver archivos internos en cuadros | Clic derecho: Borrar o Abrir");
            }
            else if (tr.Node != null)
            {
                safety = SafetyAdvisor.AssessNode(tr.Node);
                DateTime created = tr.Node.CreatedDate;
                DateTime modified = tr.Node.LastModified;

                if ((created.Year <= 1980 || modified.Year <= 1980) && !string.IsNullOrEmpty(tr.Node.FullPath))
                {
                    try
                    {
                        if (tr.Node.IsFolder && System.IO.Directory.Exists(tr.Node.FullPath))
                        {
                            if (created.Year <= 1980) created = System.IO.Directory.GetCreationTime(tr.Node.FullPath);
                            if (modified.Year <= 1980) modified = System.IO.Directory.GetLastWriteTime(tr.Node.FullPath);
                            tr.Node.CreatedDate = created;
                            tr.Node.LastModified = modified;
                        }
                        else if (!tr.Node.IsFolder && System.IO.File.Exists(tr.Node.FullPath))
                        {
                            if (created.Year <= 1980) created = System.IO.File.GetCreationTime(tr.Node.FullPath);
                            if (modified.Year <= 1980) modified = System.IO.File.GetLastWriteTime(tr.Node.FullPath);
                            tr.Node.CreatedDate = created;
                            tr.Node.LastModified = modified;
                        }
                    }
                    catch { }
                }

                lines.Add("📅 Fecha agregado al disco: " + SafetyAdvisor.FormatDateWithRelativeAge(created));
                if (modified.Year > 1980)
                    lines.Add("🕒 Última modificación: " + SafetyAdvisor.FormatDateWithRelativeAge(modified));

                string kind = tr.Node.IsFolder
                    ? string.Format("Carpeta ({0} archivos, {1} subcarpetas)", FormatUtils.FormatNumber(tr.Node.FileCount), FormatUtils.FormatNumber(tr.Node.FolderCount))
                    : ("Archivo " + (string.IsNullOrEmpty(tr.Node.Extension) ? "" : tr.Node.Extension.ToUpperInvariant()));

                if (tr.Node.MatchedInstall != null)
                {
                    kind = "Instalación: " + tr.Node.MatchedInstall.Name + " (" + kind + ")";
                }
                lines.Add("📂 Tipo: " + kind);

                if (safety != null)
                {
                    lines.Add("SAFETY_BADGE:" + safety.VerdictBadge);
                    lines.Add("ℹ️ ¿Qué es?: " + safety.WhatIsIt);
                    lines.Add("🛡️ Consejo: " + safety.DeleteAdvice);
                }

                if (!string.IsNullOrEmpty(tr.Node.FullPath))
                    lines.Add("📍 Ruta: " + tr.Node.FullPath);

                if (tr.Node.IsFolder)
                    lines.Add("💡 Doble clic: Entrar a carpeta (Zoom) | Clic derecho: Opciones de Borrado / Abrir");
                else
                    lines.Add("💡 Clic derecho: Enviar a Papelera, Borrar Permanente (Admin) o Abrir en Windows");
            }
            else if (!string.IsNullOrEmpty(tr.Subtitle))
            {
                lines.Add(tr.Subtitle);
            }

            int maxW = 490;
            SizeF titleSize = g.MeasureString(title, _fontTooltipTitle, maxW);
            float boxW = Math.Min(maxW, Math.Max(310f, titleSize.Width + 28f));
            float boxH = titleSize.Height + 18f;

            for (int i = 0; i < lines.Count; i++)
            {
                string displayLine = lines[i].StartsWith("SAFETY_BADGE:") ? lines[i].Substring("SAFETY_BADGE:".Length) : lines[i];
                Font lineFont = lines[i].StartsWith("SAFETY_BADGE:") ? _fontGroupTitle : _fontTooltipBody;
                SizeF ls = g.MeasureString(displayLine, lineFont, maxW);
                if (ls.Width + 28f > boxW) boxW = Math.Min(maxW + 24, ls.Width + 28f);
                boxH += ls.Height + (lines[i].StartsWith("SAFETY_BADGE:") ? 8f : 3.5f);
            }

            int x = mouse.X + 16;
            int y = mouse.Y + 16;
            if (x + (int)boxW > this.ClientSize.Width - 6)
                x = Math.Max(6, mouse.X - (int)boxW - 12);
            if (y + (int)boxH > this.ClientSize.Height - 6)
                y = Math.Max(6, mouse.Y - (int)boxH - 12);

            Rectangle tipRect = new Rectangle(x, y, (int)boxW, (int)boxH);

            using (SolidBrush bBg = new SolidBrush(Color.FromArgb(248, 15, 23, 42)))
            using (Pen pBorder = new Pen(safety != null ? safety.BadgeColor : Color.FromArgb(96, 165, 250), 2.0f))
            {
                g.FillRectangle(bBg, tipRect);
                g.DrawRectangle(pBorder, tipRect);
            }

            float curY = tipRect.Y + 8f;
            using (SolidBrush bTitle = new SolidBrush(Color.FromArgb(250, 204, 21)))
            {
                g.DrawString(title, _fontTooltipTitle, bTitle, new RectangleF(tipRect.X + 10, curY, tipRect.Width - 20, titleSize.Height + 4));
                curY += titleSize.Height + 5f;
            }

            using (SolidBrush bBody = new SolidBrush(Color.FromArgb(226, 232, 240)))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    string raw = lines[i];
                    if (raw.StartsWith("SAFETY_BADGE:"))
                    {
                        string badgeTxt = raw.Substring("SAFETY_BADGE:".Length);
                        SizeF ls = g.MeasureString(badgeTxt, _fontGroupTitle, tipRect.Width - 24);
                        RectangleF pillRect = new RectangleF(tipRect.X + 8, curY + 1, tipRect.Width - 16, ls.Height + 5);
                        Color pillCol = safety != null ? Color.FromArgb(55, safety.BadgeColor) : Color.FromArgb(60, 59, 130, 246);
                        Color textCol = safety != null ? LightenColor(safety.BadgeColor, 0.35f) : Color.White;
                        using (SolidBrush bPill = new SolidBrush(pillCol))
                        using (Pen pPill = new Pen(safety != null ? safety.BadgeColor : Color.White, 1f))
                        using (SolidBrush bBadgeTxt = new SolidBrush(textCol))
                        {
                            g.FillRectangle(bPill, pillRect);
                            g.DrawRectangle(pPill, pillRect.X, pillRect.Y, pillRect.Width, pillRect.Height);
                            g.DrawString(badgeTxt, _fontGroupTitle, bBadgeTxt, new RectangleF(tipRect.X + 12, curY + 3, tipRect.Width - 24, ls.Height + 2));
                        }
                        curY += ls.Height + 9f;
                    }
                    else
                    {
                        SizeF ls = g.MeasureString(raw, _fontTooltipBody, tipRect.Width - 20);
                        Brush lineBrush = bBody;
                        bool disposeBrush = false;

                        if (raw.StartsWith("📅"))
                        {
                            lineBrush = new SolidBrush(Color.FromArgb(167, 243, 208)); // Soft mint green for Date Added
                            disposeBrush = true;
                        }
                        else if (raw.StartsWith("ℹ️") || raw.StartsWith("🛡️"))
                        {
                            lineBrush = new SolidBrush(Color.FromArgb(254, 240, 138)); // Soft warm yellow for Info & Advice
                            disposeBrush = true;
                        }
                        else if (raw.StartsWith("💡"))
                        {
                            lineBrush = new SolidBrush(Color.FromArgb(125, 211, 252)); // Sky blue for tip
                            disposeBrush = true;
                        }

                        g.DrawString(raw, _fontTooltipBody, lineBrush, new RectangleF(tipRect.X + 10, curY, tipRect.Width - 20, ls.Height + 2));
                        if (disposeBrush) lineBrush.Dispose();
                        curY += ls.Height + 3.5f;
                    }
                }
            }
        }

        private TreemapRect HitTest(Point pt)
        {
            return HitTestList(_rootRects, pt);
        }

        private TreemapRect HitTestList(List<TreemapRect> list, Point pt)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                TreemapRect tr = list[i];
                if (tr.Bounds.Contains(pt.X, pt.Y))
                {
                    // If user hovers on the group header bar itself, return the group folder
                    if (tr.IsGroup && tr.HeaderBounds.Contains(pt.X, pt.Y))
                    {
                        return tr;
                    }
                    if (tr.Children != null && tr.Children.Count > 0)
                    {
                        TreemapRect childHit = HitTestList(tr.Children, pt);
                        if (childHit != null) return childHit;
                    }
                    return tr;
                }
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mousePos = e.Location;
            TreemapRect hit = HitTest(e.Location);
            if (hit != _hoveredRect)
            {
                _hoveredRect = hit;
                this.Cursor = hit != null ? Cursors.Hand : Cursors.Default;
                if (ItemHovered != null) ItemHovered(hit);
            }
            if (_hoveredRect != null)
            {
                this.Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredRect != null)
            {
                _hoveredRect = null;
                this.Cursor = Cursors.Default;
                if (ItemHovered != null) ItemHovered(null);
                this.Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            TreemapRect hit = HitTest(e.Location);
            _selectedRect = hit;
            this.Invalidate();

            if (hit != null)
            {
                if (e.Button == MouseButtons.Left && ItemClicked != null)
                {
                    ItemClicked(hit);
                }
                else if (e.Button == MouseButtons.Right && ItemRightClicked != null)
                {
                    ItemRightClicked(hit, e.Location);
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                TreemapRect hit = HitTest(e.Location);
                if (hit != null && ItemDoubleClicked != null)
                {
                    ItemDoubleClicked(hit);
                }
            }
        }

        private static Color LightenColor(Color c, float amount)
        {
            int r = (int)(c.R + (255 - c.R) * amount);
            int g = (int)(c.G + (255 - c.G) * amount);
            int b = (int)(c.B + (255 - c.B) * amount);
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, r)),
                Math.Max(0, Math.Min(255, g)),
                Math.Max(0, Math.Min(255, b)));
        }

        private static Color DarkenColor(Color c, float amount)
        {
            int r = (int)(c.R * (1f - amount));
            int g = (int)(c.G * (1f - amount));
            int b = (int)(c.B * (1f - amount));
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, r)),
                Math.Max(0, Math.Min(255, g)),
                Math.Max(0, Math.Min(255, b)));
        }
    }
}
