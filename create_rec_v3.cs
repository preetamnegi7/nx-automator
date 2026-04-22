// NX Open C# Macro - Assembly Component Rotator (v3)
// Fixes:
//   1. Rotation now happens around each component's own axis (no Z drift / orbiting)
//   2. Assembly constraints are suppressed before rotating so the outlet actually moves
//   3. Current absolute orientation is read and composed, so repeated calls stack correctly
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
            string upper = allComps[i].FullName.ToUpper();
            if (autoBody   < 0 && upper.Contains("BODY")   && !upper.Contains("COVER")) autoBody   = i;
            if (autoCover  < 0 && upper.Contains("COVER"))                               autoCover  = i;
            if (autoOutlet < 0 && upper.Contains("OUTLET"))                              autoOutlet = i;
        }

        int bodyIdx, coverIdx, outletIdx;
        double coverAngle, outletAngle;
        string rotAxis;

        bool ok = ShowSelectionDialog(allComps, autoBody, autoCover, autoOutlet,
            out bodyIdx, out coverIdx, out outletIdx,
            out coverAngle, out outletAngle, out rotAxis);

        if (!ok)
        {
            lw.WriteLine("Cancelled by user.");
            return;
        }

        lw.WriteLine("Selection:");
        lw.WriteLine("  Body   (FIXED): " + allComps[bodyIdx].FullName);
        lw.WriteLine("  Cover:          " + allComps[coverIdx].FullName  + "  ->  " + coverAngle  + " deg");
        lw.WriteLine("  Outlet:         " + allComps[outletIdx].FullName + "  ->  " + outletAngle + " deg");
        lw.WriteLine("  Axis:           " + rotAxis);
        lw.WriteLine("");

        if (coverAngle != 0)
            RotateComponent(workPart, allComps[coverIdx].Comp, coverAngle, rotAxis);
        else
            lw.WriteLine("  Cover: No rotation (0 degrees)");

        if (outletAngle != 0)
            RotateComponent(workPart, allComps[outletIdx].Comp, outletAngle, rotAxis);
        else
            lw.WriteLine("  Outlet: No rotation (0 degrees)");

        lw.WriteLine("");
        lw.WriteLine("=== Done! ===");
    }


    // ─── Name helpers ────────────────────────────────────────────────────────

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
                    string dbName = proto.GetStringAttribute("DB_PART_NAME");
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


    // ─── Component tree collection ────────────────────────────────────────────

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
            string marker = child.GetChildren().Length > 0 ? "+" : " ";
            ci.TreeDisplay = indent + marker + " " + ci.FullName;
            comps.Add(ci);
            CollectComponents(child, comps, level + 1);
        }
    }


    // ─── Math helpers ─────────────────────────────────────────────────────────

    // Convert the 9-element csysMatrix returned by UF_ASSEM_ask_component_data
    // into an NX Matrix3x3.
    // UF stores column vectors: [Xcol | Ycol | Zcol] each 3 elements.
    // NX Matrix3x3 is row-major: rows are the transformed axes.
    static Matrix3x3 CsysArrayToMatrix(double[] a)
    {
        Matrix3x3 m = new Matrix3x3();
        // Col 0 (X axis): a[0],a[1],a[2]  ->  column 0 of rotation matrix
        // Col 1 (Y axis): a[3],a[4],a[5]  ->  column 1
        // Col 2 (Z axis): a[6],a[7],a[8]  ->  column 2
        m.Xx = a[0]; m.Xy = a[3]; m.Xz = a[6];
        m.Yx = a[1]; m.Yy = a[4]; m.Yz = a[7];
        m.Zx = a[2]; m.Zy = a[5]; m.Zz = a[8];
        return m;
    }

    // C = A * B  (standard matrix multiplication)
    static Matrix3x3 MatMul(Matrix3x3 A, Matrix3x3 B)
    {
        Matrix3x3 C = new Matrix3x3();
        C.Xx = A.Xx*B.Xx + A.Xy*B.Yx + A.Xz*B.Zx;
        C.Xy = A.Xx*B.Xy + A.Xy*B.Yy + A.Xz*B.Zy;
        C.Xz = A.Xx*B.Xz + A.Xy*B.Yz + A.Xz*B.Zz;
        C.Yx = A.Yx*B.Xx + A.Yy*B.Yx + A.Yz*B.Zx;
        C.Yy = A.Yx*B.Xy + A.Yy*B.Yy + A.Yz*B.Zy;
        C.Yz = A.Yx*B.Xz + A.Yy*B.Yz + A.Yz*B.Zz;
        C.Zx = A.Zx*B.Xx + A.Zy*B.Yx + A.Zz*B.Zx;
        C.Zy = A.Zx*B.Xy + A.Zy*B.Yy + A.Zz*B.Zy;
        C.Zz = A.Zx*B.Xz + A.Zy*B.Yz + A.Zz*B.Zz;
        return C;
    }

    static Matrix3x3 IdentityMatrix()
    {
        Matrix3x3 m = new Matrix3x3();
        m.Xx = 1; m.Yy = 1; m.Zz = 1;
        return m;
    }


    // ─── UF-layer helpers ─────────────────────────────────────────────────────

    // Returns the component's absolute origin and orientation (via NX UF layer).
    static bool GetComponentTransform(Component comp,
        out Point3d origin, out Matrix3x3 orient)
    {
        origin = new Point3d(0, 0, 0);
        orient = IdentityMatrix();
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            string refSet, instName;
            double[] o  = new double[3];
            double[] cm = new double[9];   // direction cosines of component CSYS axes
            double[] tf = new double[16];  // 4x4 homogeneous transform (not used here)
            ufs.Assem.AskComponentData(comp.Tag, out refSet, out instName, o, cm, tf);
            origin = new Point3d(o[0], o[1], o[2]);
            orient = CsysArrayToMatrix(cm);
            return true;
        }
        catch (Exception ex)
        {
            lw.WriteLine("  Warning: cannot read component transform (" + ex.Message + ").");
            lw.WriteLine("           Using identity – rotation may have positional drift.");
            return false;
        }
    }

    // Suppress all assembly (positioning) constraints that involve this component.
    // Returns the list of suppressed constraints so they can be restored later.
    static List<NXObject> SuppressComponentConstraints(Component comp, Part workPart)
    {
        var suppressed = new List<NXObject>();

        // Strategy A: NXOpen.Positioning.Constraint (NX 9+)
        try
        {
            foreach (NXObject obj in workPart.Constraints)
            {
                try
                {
                    NXOpen.Positioning.Constraint c = obj as NXOpen.Positioning.Constraint;
                    if (c == null || c.Suppressed) continue;

                    bool involves = false;
                    try
                    {
                        // Check each geometry in the constraint to see if it belongs
                        // to the component we care about.
                        NXOpen.Positioning.ConstraintReference[] refs = c.GetConstraintReferences();
                        foreach (var r in refs)
                        {
                            NXObject geom = r.GetGeometry();
                            if (geom != null && geom.IsOccurrence)
                            {
                                NXObject owner = geom.GetOwningComponent();
                                if (owner != null && owner.Tag == comp.Tag)
                                { involves = true; break; }
                            }
                        }
                    }
                    catch
                    {
                        // If we cannot check, be conservative and suppress it.
                        involves = true;
                    }

                    if (involves)
                    {
                        c.Suppressed = true;
                        suppressed.Add(c);
                    }
                }
                catch { }
            }
        }
        catch { }

        // Strategy B: UF layer – iterate tags for older NX builds
        if (suppressed.Count == 0)
        {
            try
            {
                UFSession ufs = UFSession.GetUFSession();
                Tag nextTag = ufs.Obj.CycleByName(workPart.Tag, "CONSTRAINT");
                while (nextTag != Tag.Null)
                {
                    try
                    {
                        NXObject obj = NXOpen.Utilities.NXObjectManager.Get(nextTag);
                        NXOpen.Positioning.Constraint c = obj as NXOpen.Positioning.Constraint;
                        if (c != null && !c.Suppressed)
                        {
                            c.Suppressed = true;
                            suppressed.Add(c);
                        }
                    }
                    catch { }
                    nextTag = ufs.Obj.CycleByName(workPart.Tag, "CONSTRAINT");
                }
            }
            catch { }
        }

        return suppressed;
    }

    static void RestoreConstraints(List<NXObject> constraints)
    {
        foreach (NXObject obj in constraints)
        {
            try
            {
                NXOpen.Positioning.Constraint c = obj as NXOpen.Positioning.Constraint;
                if (c != null) c.Suppressed = false;
            }
            catch { }
        }
    }


    // ─── Core rotation ────────────────────────────────────────────────────────

    static void RotateComponent(Part workPart, Component comp,
        double angleDegrees, string axis)
    {
        string name = GetBestName(comp);
        lw.WriteLine("  Rotating: " + name);
        lw.WriteLine("  Angle: " + angleDegrees + " deg  Axis: " + axis);

        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        // Incremental rotation matrix around the chosen world axis
        Matrix3x3 dR = new Matrix3x3();
        switch (axis)
        {
            case "X":
                dR.Xx = 1;   dR.Xy = 0;    dR.Xz = 0;
                dR.Yx = 0;   dR.Yy = cos;  dR.Yz = -sin;
                dR.Zx = 0;   dR.Zy = sin;  dR.Zz = cos;
                break;
            case "Y":
                dR.Xx = cos; dR.Xy = 0;    dR.Xz = sin;
                dR.Yx = 0;   dR.Yy = 1;    dR.Yz = 0;
                dR.Zx = -sin;dR.Zy = 0;    dR.Zz = cos;
                break;
            default: // Z
                dR.Xx = cos; dR.Xy = -sin; dR.Xz = 0;
                dR.Yx = sin; dR.Yy = cos;  dR.Yz = 0;
                dR.Zx = 0;   dR.Zy = 0;    dR.Zz = 1;
                break;
        }

        // Read the component's current absolute position and orientation.
        Point3d P;
        Matrix3x3 currentOrient;
        GetComponentTransform(comp, out P, out currentOrient);

        lw.WriteLine("  Current origin: (" +
            P.X.ToString("F4") + ", " +
            P.Y.ToString("F4") + ", " +
            P.Z.ToString("F4") + ")");

        // ── Fix 1: compensation translation ──────────────────────────────────
        // MoveComponent rotates every world-space point p  =>  dR*p + delta.
        // To keep the component's own origin fixed we need:
        //   dR * P + delta = P   =>   delta = P - dR*P = (I - dR)*P
        double px = P.X, py = P.Y, pz = P.Z;
        double rpx = dR.Xx*px + dR.Xy*py + dR.Xz*pz;
        double rpy = dR.Yx*px + dR.Yy*py + dR.Yz*pz;
        double rpz = dR.Zx*px + dR.Zy*py + dR.Zz*pz;
        Vector3d delta = new Vector3d(px - rpx, py - rpy, pz - rpz);

        lw.WriteLine("  Compensation delta: (" +
            delta.X.ToString("F4") + ", " +
            delta.Y.ToString("F4") + ", " +
            delta.Z.ToString("F4") + ")");

        // New absolute orientation = dR * currentOrient
        // (applying the incremental rotation on top of the existing orientation)
        Matrix3x3 newOrient = MatMul(dR, currentOrient);

        // ── Fix 2: suppress constraints so they don't fight the rotation ──────
        List<NXObject> suppressed = SuppressComponentConstraints(comp, workPart);
        if (suppressed.Count > 0)
            lw.WriteLine("  Suppressed " + suppressed.Count + " constraint(s).");
        else
            lw.WriteLine("  No constraints found (or already suppressed).");

        // ── Apply the move ────────────────────────────────────────────────────
        bool success = false;
        try
        {
            // Pass newOrient (absolute orientation after rotation) + delta (keeps origin fixed).
            workPart.ComponentAssembly.MoveComponent(comp, delta, newOrient);
            lw.WriteLine("  OK: rotated with composed orientation.");
            success = true;
        }
        catch (Exception ex1)
        {
            lw.WriteLine("  Attempt 1 failed (" + ex1.Message + "), trying incremental dR...");
            try
            {
                // Fallback: some NX versions prefer just the incremental dR.
                workPart.ComponentAssembly.MoveComponent(comp, delta, dR);
                lw.WriteLine("  OK: rotated with incremental dR.");
                success = true;
            }
            catch (Exception ex2)
            {
                lw.WriteLine("  ERROR: " + ex2.Message);
                lw.WriteLine("  Manual fix: in Assembly Navigator, right-click the component");
                lw.WriteLine("  -> 'Assembly Constraints' -> suppress all constraints, then retry.");
            }
        }

        // Restore constraints so the assembly stays properly constrained
        // for any subsequent operations.
        if (suppressed.Count > 0)
        {
            RestoreConstraints(suppressed);
            if (success)
                lw.WriteLine("  Constraints restored.");
        }

        lw.WriteLine("");
    }


    // ─── Selection dialog (unchanged from v2) ─────────────────────────────────

    static bool ShowSelectionDialog(List<CompInfo> allComps,
        int autoBody, int autoCover, int autoOutlet,
        out int bodyIdx, out int coverIdx, out int outletIdx,
        out double coverAngle, out double outletAngle, out string rotAxis)
    {
        bodyIdx = 0; coverIdx = 0; outletIdx = 0;
        coverAngle = 0; outletAngle = 0; rotAxis = "Z";

        Form form = new Form();
        form.Text = "Assembly Component Rotator v3";
        form.Width = 620;
        form.Height = 580;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = true;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Segoe UI", 9);

        int y = 12;
        int labelX = 15, comboX = 15, comboW = 570;

        Label title = new Label();
        title.Text = "ASSEMBLY COMPONENT ROTATOR  v3";
        title.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(0, 90, 158);
        title.Left = labelX; title.Top = y; title.Width = 540;
        form.Controls.Add(title); y += 28;

        Label subtitle = new Label();
        subtitle.Text = "Constraints are suppressed before rotating and restored after.";
        subtitle.ForeColor = Color.Gray;
        subtitle.Left = labelX; subtitle.Top = y; subtitle.Width = 540;
        form.Controls.Add(subtitle); y += 30;

        Panel sep1 = new Panel();
        sep1.BackColor = Color.FromArgb(220, 220, 220);
        sep1.Left = labelX; sep1.Top = y; sep1.Width = comboW; sep1.Height = 1;
        form.Controls.Add(sep1); y += 12;

        // BODY
        AddLabel(form, "BODY  (fixed – will NOT move)", new Font("Segoe UI", 9, FontStyle.Bold),
            Color.FromArgb(0, 100, 200), labelX, y, 400); y += 20;
        ComboBox bodyCombo = AddCombo(form, allComps, comboX, y, comboW,
            autoBody >= 0 ? autoBody : 0); y += 35;

        // COVER
        AddLabel(form, "COVER  (will be rotated)", new Font("Segoe UI", 9, FontStyle.Bold),
            Color.FromArgb(180, 0, 0), labelX, y, 300); y += 20;
        ComboBox coverCombo = AddCombo(form, allComps, comboX, y, comboW,
            autoCover >= 0 ? autoCover : Math.Min(1, allComps.Count - 1)); y += 28;
        AddLabel(form, "Rotation angle:", form.Font, Color.Black, labelX, y + 3, 110);
        TextBox coverAngleBox = AddTextBox(form, "0", 130, y, 80); y += 38;

        // OUTLET
        AddLabel(form, "OUTLET  (will be rotated)", new Font("Segoe UI", 9, FontStyle.Bold),
            Color.FromArgb(0, 130, 0), labelX, y, 300); y += 20;
        ComboBox outletCombo = AddCombo(form, allComps, comboX, y, comboW,
            autoOutlet >= 0 ? autoOutlet : Math.Min(2, allComps.Count - 1)); y += 28;
        AddLabel(form, "Rotation angle:", form.Font, Color.Black, labelX, y + 3, 110);
        TextBox outletAngleBox = AddTextBox(form, "0", 130, y, 80); y += 38;

        Panel sep2 = new Panel();
        sep2.BackColor = Color.FromArgb(220, 220, 220);
        sep2.Left = labelX; sep2.Top = y; sep2.Width = comboW; sep2.Height = 1;
        form.Controls.Add(sep2); y += 12;

        // Axis
        AddLabel(form, "Rotation Axis:", new Font("Segoe UI", 9, FontStyle.Bold),
            Color.Black, labelX, y, 120);
        RadioButton radioZ = AddRadio(form, "Z-Axis (vertical)", true,  140, y, 145);
        RadioButton radioX = AddRadio(form, "X-Axis",           false, 290, y, 100);
        RadioButton radioY = AddRadio(form, "Y-Axis",           false, 400, y, 100);
        y += 45;

        Button okBtn = new Button();
        okBtn.Text = "Apply Rotation";
        okBtn.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        okBtn.BackColor = Color.FromArgb(0, 120, 215);
        okBtn.ForeColor = Color.White;
        okBtn.FlatStyle = FlatStyle.Flat;
        okBtn.FlatAppearance.BorderSize = 0;
        okBtn.Left = labelX; okBtn.Top = y; okBtn.Width = 275; okBtn.Height = 42;
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

        form.AcceptButton = okBtn;
        form.CancelButton = cancelBtn;

        if (form.ShowDialog() != DialogResult.OK) { form.Dispose(); return false; }

        bodyIdx   = bodyCombo.SelectedIndex;
        coverIdx  = coverCombo.SelectedIndex;
        outletIdx = outletCombo.SelectedIndex;
        if (!double.TryParse(coverAngleBox.Text,  out coverAngle))  coverAngle  = 0;
        if (!double.TryParse(outletAngleBox.Text, out outletAngle)) outletAngle = 0;
        rotAxis = radioX.Checked ? "X" : (radioY.Checked ? "Y" : "Z");

        form.Dispose();
        return true;
    }

    // ─── Dialog control helpers ───────────────────────────────────────────────

    static void AddLabel(Form f, string text, Font font, Color color, int x, int y, int w)
    {
        Label l = new Label { Text = text, Font = font, ForeColor = color,
                              Left = x, Top = y, Width = w };
        f.Controls.Add(l);
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
