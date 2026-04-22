// NX Open C# Macro - Assembly Component Rotator (v3)
//
// Core approach:
//   UF_ASSEM_reposition_instance  writes the 4x4 transform directly into NX's
//   internal data, bypassing the constraint solver completely.  This is why
//   MoveComponent silently failed on the outlet even after constraint suppression.
//
//   AskAbsoccTransform  reads the component's current absolute 4x4 transform so
//   we can (a) auto-fill the pivot from the body origin and (b) compute the exact
//   new transform including composition with any existing tilt.
//
// Usage: Open an assembly, then run via Tools -> Journal -> Play

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Assemblies;
using NXOpen.UF;

public class AssemblyRotator
{
    static Session theSession = Session.GetSession();
    static UI theUI = UI.GetUI();
    static ListingWindow lw = theSession.ListingWindow;

    class CompInfo
    {
        public Component Comp;
        public string FullName;
        public string ShortName;
        public int Level;
        public string TreeDisplay;
    }

    public static void Main(string[] args)
    {
        lw.Open();
        lw.WriteLine("=== Assembly Component Rotator v3 ===");
        lw.WriteLine("");

        Part workPart = theSession.Parts.Work;
        if (workPart == null)
        {
            MessageBox.Show("Please open an assembly first.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ComponentAssembly asm = workPart.ComponentAssembly;
        Component rootComp = asm.RootComponent;
        if (rootComp == null)
        {
            MessageBox.Show("No assembly is open. Please open an assembly file.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        List<CompInfo> allComps = new List<CompInfo>();
        CollectComponents(rootComp, allComps, 0);
        if (allComps.Count == 0)
        {
            MessageBox.Show("No components found in this assembly.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        lw.WriteLine("Assembly Tree:");
        lw.WriteLine("─────────────────────────────────────────────");
        lw.WriteLine("  [ROOT] " + GetBestName(rootComp));
        foreach (CompInfo ci in allComps)
        {
            string indent = new string(' ', (ci.Level + 1) * 4);
            string prefix = ci.Comp.GetChildren().Length > 0 ? "[ASM]" : "[PRT]";
            lw.WriteLine(indent + prefix + " " + ci.FullName);
        }
        lw.WriteLine("─────────────────────────────────────────────");
        lw.WriteLine("Found " + allComps.Count + " components");
        lw.WriteLine("");

        // Auto-detect body, cover, outlet by name
        int autoBody = -1, autoCover = -1, autoOutlet = -1;
        for (int i = 0; i < allComps.Count; i++)
        {
            string up = allComps[i].FullName.ToUpper();
            if (autoBody   < 0 && up.Contains("BODY")   && !up.Contains("COVER")) autoBody   = i;
            if (autoCover  < 0 && up.Contains("COVER"))                             autoCover  = i;
            if (autoOutlet < 0 && up.Contains("OUTLET"))                            autoOutlet = i;
        }

        // Auto-read body origin to pre-fill pivot
        Point3d defaultPivot = new Point3d(0, 0, 0);
        if (autoBody >= 0)
        {
            double[,] bodyT = GetAbsoluteTransform(allComps[autoBody].Comp);
            defaultPivot = ExtractOrigin(bodyT);
            lw.WriteLine("Auto-pivot from body origin: (" +
                defaultPivot.X.ToString("F3") + ", " +
                defaultPivot.Y.ToString("F3") + ", " +
                defaultPivot.Z.ToString("F3") + ")");
            lw.WriteLine("");
        }

        int bodyIdx, coverIdx, outletIdx;
        double coverAngle, outletAngle;
        string rotAxis;
        Point3d pivot;

        bool ok = ShowSelectionDialog(allComps, autoBody, autoCover, autoOutlet,
            defaultPivot,
            out bodyIdx, out coverIdx, out outletIdx,
            out coverAngle, out outletAngle, out rotAxis, out pivot);

        if (!ok) { lw.WriteLine("Cancelled by user."); return; }

        lw.WriteLine("Selection:");
        lw.WriteLine("  Body   (FIXED): " + allComps[bodyIdx].FullName);
        lw.WriteLine("  Cover:          " + allComps[coverIdx].FullName  + "  ->  " + coverAngle  + " deg");
        lw.WriteLine("  Outlet:         " + allComps[outletIdx].FullName + "  ->  " + outletAngle + " deg");
        lw.WriteLine("  Axis:           " + rotAxis);
        lw.WriteLine("  Pivot:          (" + pivot.X + ", " + pivot.Y + ", " + pivot.Z + ")");
        lw.WriteLine("");

        if (coverAngle  != 0) RotateComponent(workPart, allComps[coverIdx].Comp,  coverAngle,  rotAxis, pivot);
        else lw.WriteLine("  Cover:  No rotation (0 degrees)");

        if (outletAngle != 0) RotateComponent(workPart, allComps[outletIdx].Comp, outletAngle, rotAxis, pivot);
        else lw.WriteLine("  Outlet: No rotation (0 degrees)");

        lw.WriteLine("");
        lw.WriteLine("=== Done! ===");
    }


    // ─── Name helpers ─────────────────────────────────────────────────────────

    static string GetBestName(Component comp)
    {
        string best = "";
        TrySet(ref best, () => comp.DisplayName);
        TrySet(ref best, () => comp.Name);
        TrySet(ref best, () => comp.JournalIdentifier);
        try
        {
            Part proto = (Part)comp.Prototype;
            if (proto != null)
            {
                string leaf = proto.Leaf;
                if (!string.IsNullOrEmpty(leaf))
                {
                    if (!best.ToUpper().Contains(leaf.ToUpper()) &&
                        !leaf.ToUpper().Contains(best.ToUpper()))
                        best = best + "  [" + leaf + "]";
                    else if (leaf.Length > best.Length)
                        best = leaf;
                }
                try
                {
                    NXObject.AttributeInformation attr =
                        proto.GetUserAttribute("DB_PART_NAME", NXObject.AttributeType.String, -1);
                    string dbName = attr.StringValue;
                    if (!string.IsNullOrEmpty(dbName) && !best.ToUpper().Contains(dbName.ToUpper()))
                        best = best + "  (" + dbName + ")";
                }
                catch { }
            }
        }
        catch { }
        return string.IsNullOrEmpty(best) ? "(unnamed component)" : best;
    }

    static void TrySet(ref string best, Func<string> getter)
    {
        try { string v = getter(); if (!string.IsNullOrEmpty(v) && v.Length > best.Length) best = v; }
        catch { }
    }


    // ─── Component tree ────────────────────────────────────────────────────────

    static void CollectComponents(Component parent, List<CompInfo> comps, int level)
    {
        foreach (Component child in parent.GetChildren())
        {
            CompInfo ci = new CompInfo();
            ci.Comp = child;
            ci.Level = level;
            ci.FullName = GetBestName(child);
            ci.ShortName = ci.FullName;
            string indent = new string(' ', level * 4);
            ci.TreeDisplay = indent + (child.GetChildren().Length > 0 ? "+ " : "  ") + ci.FullName;
            comps.Add(ci);
            CollectComponents(child, comps, level + 1);
        }
    }


    // ─── 4×4 transform helpers ─────────────────────────────────────────────────
    //
    // NX column-major convention for double[4,4]:
    //   T[0, 0..2]  =  X-axis direction  (col 0)
    //   T[1, 0..2]  =  Y-axis direction  (col 1)
    //   T[2, 0..2]  =  Z-axis direction  (col 2)
    //   T[3, 0..2]  =  origin / translation  (col 3)
    //   T[*, 3]     =  homogeneous row  (0,0,0,1)
    //
    // If the logged "Col-major origin" values look wrong but "Row-major origin"
    // values look right, swap the two conventions in ExtractOrigin / BuildNewTransform.

    static double[,] GetAbsoluteTransform(Component comp)
    {
        double[,] T = new double[4, 4];
        T[0,0] = T[1,1] = T[2,2] = T[3,3] = 1.0;   // identity fallback
        try
        {
            UFSession.GetUFSession().Assem.AskAbsoccTransform(comp.Tag, T);
        }
        catch (Exception ex)
        {
            lw.WriteLine("  Warning: AskAbsoccTransform failed (" + ex.Message + "). Using identity.");
        }
        return T;
    }

    // Reads origin from column-major transform.
    // Logs both interpretations so you can verify which is correct for your NX build.
    static Point3d ExtractOrigin(double[,] T)
    {
        // Column-major (typical NX UF convention): origin = T[3, 0..2]
        Point3d colMajor = new Point3d(T[3, 0], T[3, 1], T[3, 2]);
        // Row-major (alternative): origin = T[0..2, 3]
        Point3d rowMajor = new Point3d(T[0, 3], T[1, 3], T[2, 3]);

        lw.WriteLine("    ColMajor origin: (" + colMajor.X.ToString("F3") + ", " +
            colMajor.Y.ToString("F3") + ", " + colMajor.Z.ToString("F3") + ")");
        lw.WriteLine("    RowMajor origin: (" + rowMajor.X.ToString("F3") + ", " +
            rowMajor.Y.ToString("F3") + ", " + rowMajor.Z.ToString("F3") + ")");

        // Use column-major by default; change to rowMajor if your assembly shows wrong values
        return colMajor;
    }

    // Build incremental 3×3 rotation matrix (dR[row, col])
    static double[,] BuildDR(double angleDeg, string axis)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        double[,] dR = new double[3, 3];
        switch (axis)
        {
            case "X":
                dR[0,0]=1; dR[0,1]=0; dR[0,2]=0;
                dR[1,0]=0; dR[1,1]=c;  dR[1,2]=-s;
                dR[2,0]=0; dR[2,1]=s;  dR[2,2]=c;
                break;
            case "Y":
                dR[0,0]=c;  dR[0,1]=0; dR[0,2]=s;
                dR[1,0]=0;  dR[1,1]=1; dR[1,2]=0;
                dR[2,0]=-s; dR[2,1]=0; dR[2,2]=c;
                break;
            default: // Z
                dR[0,0]=c;  dR[0,1]=-s; dR[0,2]=0;
                dR[1,0]=s;  dR[1,1]=c;  dR[1,2]=0;
                dR[2,0]=0;  dR[2,1]=0;  dR[2,2]=1;
                break;
        }
        return dR;
    }

    // Compute new 4×4 transform: rotate existing orientation and position around pivot.
    // Column-major: axis columns are T[0..2, 0..2], origin is T[3, 0..2].
    static double[,] BuildNewTransform(double[,] T, double[,] dR, Point3d pivot)
    {
        double[,] Tn = new double[4, 4];
        Tn[3, 3] = 1.0;

        // Rotate each axis column: new_col = dR * old_col
        for (int col = 0; col < 3; col++)
        {
            for (int row = 0; row < 3; row++)
            {
                Tn[col, row] = dR[row, 0] * T[col, 0]
                             + dR[row, 1] * T[col, 1]
                             + dR[row, 2] * T[col, 2];
            }
            Tn[col, 3] = 0.0;
        }

        // Rotate origin around pivot: new_o = dR * (o - P) + P
        double ox = T[3, 0] - pivot.X;
        double oy = T[3, 1] - pivot.Y;
        double oz = T[3, 2] - pivot.Z;
        Tn[3, 0] = dR[0,0]*ox + dR[0,1]*oy + dR[0,2]*oz + pivot.X;
        Tn[3, 1] = dR[1,0]*ox + dR[1,1]*oy + dR[1,2]*oz + pivot.Y;
        Tn[3, 2] = dR[2,0]*ox + dR[2,1]*oy + dR[2,2]*oz + pivot.Z;
        Tn[3, 3] = 1.0;
        return Tn;
    }

    // Convert 3×3 double[,] to NX Matrix3x3 (for MoveComponent fallback)
    static Matrix3x3 ToNXMatrix(double[,] dR)
    {
        Matrix3x3 m = new Matrix3x3();
        m.Xx = dR[0,0]; m.Xy = dR[0,1]; m.Xz = dR[0,2];
        m.Yx = dR[1,0]; m.Yy = dR[1,1]; m.Yz = dR[1,2];
        m.Zx = dR[2,0]; m.Zy = dR[2,1]; m.Zz = dR[2,2];
        return m;
    }


    // ─── Constraint suppression (belt-and-suspenders alongside RepositionInstance)

    static List<NXOpen.Positioning.Constraint> SuppressAllConstraints(Part workPart)
    {
        var suppressed = new List<NXOpen.Positioning.Constraint>();
        int total = 0;
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            Tag tag = Tag.Null;
            ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref tag);
            while (tag != Tag.Null)
            {
                total++;
                try
                {
                    NXOpen.Positioning.Constraint c =
                        NXOpen.Utilities.NXObjectManager.Get(tag) as NXOpen.Positioning.Constraint;
                    if (c != null && !c.Suppressed)
                    {
                        c.Suppressed = true;
                        suppressed.Add(c);
                    }
                }
                catch { }
                ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref tag);
            }
        }
        catch (Exception ex)
        {
            lw.WriteLine("  Warning: CycleObjsInPart failed (" + ex.Message + ").");
        }
        lw.WriteLine("  Objects scanned: " + total + "  |  Constraints suppressed: " + suppressed.Count);
        return suppressed;
    }

    static void RestoreConstraints(List<NXOpen.Positioning.Constraint> list)
    {
        foreach (var c in list)
            try { c.Suppressed = false; } catch { }
    }


    // ─── Core rotation ────────────────────────────────────────────────────────

    static void RotateComponent(Part workPart, Component comp,
        double angleDeg, string axis, Point3d pivot)
    {
        string name = GetBestName(comp);
        lw.WriteLine("─── Rotating: " + name + " ───");
        lw.WriteLine("  Angle: " + angleDeg + " deg   Axis: " + axis);
        lw.WriteLine("  Pivot: (" + pivot.X.ToString("F3") + ", " +
            pivot.Y.ToString("F3") + ", " + pivot.Z.ToString("F3") + ")");

        // Read current transform and log origin
        double[,] T = GetAbsoluteTransform(comp);
        lw.WriteLine("  Current component origin:");
        Point3d originBefore = ExtractOrigin(T);

        // Build incremental rotation and new full transform
        double[,] dR  = BuildDR(angleDeg, axis);
        double[,] T_new = BuildNewTransform(T, dR, pivot);

        // Suppress constraints (belt-and-suspenders; RepositionInstance works without this too)
        var suppressed = SuppressAllConstraints(workPart);

        bool success = false;

        // ── Primary: RepositionInstance ────────────────────────────────────────
        // Writes the 4×4 transform matrix directly into the part occurrence data,
        // completely bypassing the constraint solver.  This is why MoveComponent
        // fails on fully-constrained components.
        try
        {
            UFSession.GetUFSession().Assem.RepositionInstance(comp.Tag, T_new);
            lw.WriteLine("  RepositionInstance: OK");
            success = true;
        }
        catch (Exception ex1)
        {
            lw.WriteLine("  RepositionInstance failed: " + ex1.Message);

            // ── Fallback: MoveComponent with pivot-compensation delta ───────────
            try
            {
                double px = pivot.X, py = pivot.Y, pz = pivot.Z;
                double rpx = dR[0,0]*px + dR[0,1]*py + dR[0,2]*pz;
                double rpy = dR[1,0]*px + dR[1,1]*py + dR[1,2]*pz;
                double rpz = dR[2,0]*px + dR[2,1]*py + dR[2,2]*pz;
                Vector3d delta = new Vector3d(px - rpx, py - rpy, pz - rpz);

                workPart.ComponentAssembly.MoveComponent(comp, delta, ToNXMatrix(dR));
                lw.WriteLine("  MoveComponent fallback: OK");
                success = true;
            }
            catch (Exception ex2)
            {
                lw.WriteLine("  MoveComponent fallback failed: " + ex2.Message);
                lw.WriteLine("  → Manual fix: Assembly Navigator -> right-click component");
                lw.WriteLine("    -> Make Work Part, then reposition manually.");
            }
        }

        // Verify: read origin after move
        if (success)
        {
            double[,] T_after = GetAbsoluteTransform(comp);
            lw.WriteLine("  After-rotation component origin:");
            Point3d originAfter = ExtractOrigin(T_after);
            _ = originBefore;   // suppress unused warning; both are logged above
            _ = originAfter;
        }

        RestoreConstraints(suppressed);
        if (suppressed.Count > 0) lw.WriteLine("  Constraints restored.");
        lw.WriteLine("");
    }


    // ─── Selection dialog ─────────────────────────────────────────────────────

    static bool ShowSelectionDialog(List<CompInfo> allComps,
        int autoBody, int autoCover, int autoOutlet,
        Point3d defaultPivot,
        out int bodyIdx, out int coverIdx, out int outletIdx,
        out double coverAngle, out double outletAngle,
        out string rotAxis, out Point3d pivot)
    {
        bodyIdx = 0; coverIdx = 0; outletIdx = 0;
        coverAngle = 0; outletAngle = 0; rotAxis = "Z";
        pivot = defaultPivot;

        Form form = new Form();
        form.Text = "Assembly Component Rotator v3";
        form.Width = 620; form.Height = 700;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = true;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false; form.MinimizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Segoe UI", 9);

        int y = 12;
        const int lx = 15, cw = 570;

        // Title
        AddLabel(form, "ASSEMBLY COMPONENT ROTATOR  v3",
            new Font("Segoe UI", 11, FontStyle.Bold), Color.FromArgb(0, 90, 158), lx, y, 540); y += 28;
        AddLabel(form,
            "Uses RepositionInstance to bypass constraints. Pivot auto-detected from body origin.",
            form.Font, Color.Gray, lx, y, 560); y += 30;

        HSep(form, lx, y, cw); y += 12;

        // BODY
        AddLabel(form, "BODY  (fixed – will NOT move)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0, 100, 200), lx, y, 400); y += 20;
        ComboBox bodyCombo = AddCombo(form, allComps, lx, y, cw,
            autoBody >= 0 ? autoBody : 0); y += 35;

        // COVER
        AddLabel(form, "COVER  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(180, 0, 0), lx, y, 300); y += 20;
        ComboBox coverCombo = AddCombo(form, allComps, lx, y, cw,
            autoCover >= 0 ? autoCover : Math.Min(1, allComps.Count - 1)); y += 28;
        AddLabel(form, "Rotation angle:", form.Font, Color.Black, lx, y + 3, 110);
        TextBox coverAngleBox = AddTextBox(form, "0", lx + 115, y, 80); y += 38;

        // OUTLET
        AddLabel(form, "OUTLET  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0, 130, 0), lx, y, 300); y += 20;
        ComboBox outletCombo = AddCombo(form, allComps, lx, y, cw,
            autoOutlet >= 0 ? autoOutlet : Math.Min(2, allComps.Count - 1)); y += 28;
        AddLabel(form, "Rotation angle:", form.Font, Color.Black, lx, y + 3, 110);
        TextBox outletAngleBox = AddTextBox(form, "0", lx + 115, y, 80); y += 38;

        HSep(form, lx, y, cw); y += 12;

        // Rotation axis
        AddLabel(form, "Rotation Axis:",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.Black, lx, y, 120);
        RadioButton radioZ = AddRadio(form, "Z-Axis (vertical)", true,  140, y, 145);
        RadioButton radioX = AddRadio(form, "X-Axis",           false, 290, y, 100);
        RadioButton radioY = AddRadio(form, "Y-Axis",           false, 400, y, 100);
        y += 36;

        HSep(form, lx, y, cw); y += 12;

        // Pivot point
        AddLabel(form,
            "Rotation Axis Point  (auto-detected from body origin – override if needed):",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(80, 0, 120), lx, y, 560); y += 22;

        AddLabel(form, "X:", form.Font, Color.Black, lx,       y + 3, 18);
        TextBox pivX = AddTextBox(form, defaultPivot.X.ToString("F3"), lx + 18,   y, 70);
        AddLabel(form, "Y:", form.Font, Color.Black, lx + 100, y + 3, 18);
        TextBox pivY = AddTextBox(form, defaultPivot.Y.ToString("F3"), lx + 118,  y, 70);
        AddLabel(form, "Z:", form.Font, Color.Black, lx + 200, y + 3, 18);
        TextBox pivZ = AddTextBox(form, defaultPivot.Z.ToString("F3"), lx + 218,  y, 70);

        AddLabel(form, "Tip: if the auto value looks wrong, use NX Information > Object on the body.",
            form.Font, Color.Gray, lx, y + 26, 560);
        y += 56;

        // Buttons
        Button okBtn = new Button();
        okBtn.Text = "Apply Rotation";
        okBtn.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        okBtn.BackColor = Color.FromArgb(0, 120, 215); okBtn.ForeColor = Color.White;
        okBtn.FlatStyle = FlatStyle.Flat; okBtn.FlatAppearance.BorderSize = 0;
        okBtn.Left = lx; okBtn.Top = y; okBtn.Width = 275; okBtn.Height = 42;
        okBtn.DialogResult = DialogResult.OK;
        form.Controls.Add(okBtn);

        Button cancelBtn = new Button();
        cancelBtn.Text = "Cancel";
        cancelBtn.Font = new Font("Segoe UI", 10);
        cancelBtn.FlatStyle = FlatStyle.Flat;
        cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
        cancelBtn.Left = 310; cancelBtn.Top = y; cancelBtn.Width = 275; cancelBtn.Height = 42;
        cancelBtn.DialogResult = DialogResult.Cancel;
        form.Controls.Add(cancelBtn);

        form.AcceptButton = okBtn; form.CancelButton = cancelBtn;

        if (form.ShowDialog() != DialogResult.OK) { form.Dispose(); return false; }

        bodyIdx   = bodyCombo.SelectedIndex;
        coverIdx  = coverCombo.SelectedIndex;
        outletIdx = outletCombo.SelectedIndex;
        if (!double.TryParse(coverAngleBox.Text,  out coverAngle))  coverAngle  = 0;
        if (!double.TryParse(outletAngleBox.Text, out outletAngle)) outletAngle = 0;
        rotAxis = radioX.Checked ? "X" : (radioY.Checked ? "Y" : "Z");

        double px = 0, py = 0, pz = 0;
        double.TryParse(pivX.Text, out px);
        double.TryParse(pivY.Text, out py);
        double.TryParse(pivZ.Text, out pz);
        pivot = new Point3d(px, py, pz);

        form.Dispose();
        return true;
    }


    // ─── Dialog control helpers ───────────────────────────────────────────────

    static void HSep(Form f, int x, int y, int w)
    {
        Panel p = new Panel { BackColor = Color.FromArgb(220,220,220),
                              Left = x, Top = y, Width = w, Height = 1 };
        f.Controls.Add(p);
    }

    static void AddLabel(Form f, string text, Font font, Color color, int x, int y, int w)
    {
        f.Controls.Add(new Label { Text = text, Font = font, ForeColor = color,
                                   Left = x, Top = y, Width = w });
    }

    static ComboBox AddCombo(Form f, List<CompInfo> items, int x, int y, int w, int sel)
    {
        ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
            Left = x, Top = y, Width = w, DropDownWidth = 700,
            Font = new Font("Consolas", 9) };
        foreach (CompInfo ci in items) cb.Items.Add(ci.TreeDisplay);
        cb.SelectedIndex = Math.Max(0, Math.Min(sel, items.Count - 1));
        f.Controls.Add(cb);
        return cb;
    }

    static TextBox AddTextBox(Form f, string text, int x, int y, int w)
    {
        TextBox tb = new TextBox { Text = text, Left = x, Top = y, Width = w,
                                   TextAlign = HorizontalAlignment.Center };
        f.Controls.Add(tb);
        return tb;
    }

    static RadioButton AddRadio(Form f, string text, bool check, int x, int y, int w)
    {
        RadioButton r = new RadioButton { Text = text, Checked = check,
                                          Left = x, Top = y, Width = w };
        f.Controls.Add(r);
        return r;
    }
}
