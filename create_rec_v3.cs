// NX Open C# Macro - Assembly Component Rotator (v3)
// Fixes vs v2:
//   1. Rotation pivot point entered in dialog – eliminates Z drift / orbiting entirely
//   2. Constraints suppressed via UFObj.CycleObjsInPart (correct C# signature)
//   3. Compensation delta keeps pivot stationary during MoveComponent
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
        Point3d pivot;

        bool ok = ShowSelectionDialog(allComps, autoBody, autoCover, autoOutlet,
            out bodyIdx, out coverIdx, out outletIdx,
            out coverAngle, out outletAngle, out rotAxis, out pivot);

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
        lw.WriteLine("  Pivot:          (" + pivot.X + ", " + pivot.Y + ", " + pivot.Z + ")");
        lw.WriteLine("");

        if (coverAngle != 0)
            RotateComponent(workPart, allComps[coverIdx].Comp, coverAngle, rotAxis, pivot);
        else
            lw.WriteLine("  Cover: No rotation (0 degrees)");

        if (outletAngle != 0)
            RotateComponent(workPart, allComps[outletIdx].Comp, outletAngle, rotAxis, pivot);
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
                // FIX: GetStringAttribute is deprecated in NX8+; use GetUserAttribute
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


    // ─── Constraint helpers ───────────────────────────────────────────────────

    // Suppress every NXOpen.Positioning.Constraint found in the work part.
    // Uses UFObj.CycleObjsInPart (correct ref-Tag signature; type -1 = all objects).
    // Returns the list so they can be restored after the move.
    static List<NXOpen.Positioning.Constraint> SuppressAllConstraints(Part workPart)
    {
        var suppressed = new List<NXOpen.Positioning.Constraint>();
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            Tag cycleTag = Tag.Null;
            ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref cycleTag);   // -1 = UF_OBJ_NO_TYPE (all)
            while (cycleTag != Tag.Null)
            {
                try
                {
                    // NXObjectManager.Get returns TaggedObject; cast directly to what we need.
                    NXOpen.Positioning.Constraint c =
                        NXOpen.Utilities.NXObjectManager.Get(cycleTag) as NXOpen.Positioning.Constraint;
                    if (c != null && !c.Suppressed)
                    {
                        c.Suppressed = true;
                        suppressed.Add(c);
                    }
                }
                catch { }
                ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref cycleTag);
            }
        }
        catch (Exception ex)
        {
            lw.WriteLine("  Warning: constraint suppression failed (" + ex.Message + ").");
            lw.WriteLine("           Manually suppress constraints if rotation is blocked.");
        }
        return suppressed;
    }

    static void RestoreConstraints(List<NXOpen.Positioning.Constraint> constraints)
    {
        foreach (NXOpen.Positioning.Constraint c in constraints)
        {
            try { c.Suppressed = false; }
            catch { }
        }
    }


    // ─── Core rotation ────────────────────────────────────────────────────────

    // Rotates 'comp' by 'angleDegrees' around the specified world axis,
    // keeping 'pivot' stationary (pivot = a point on the rotation axis,
    // typically the body/assembly origin entered by the user).
    static void RotateComponent(Part workPart, Component comp,
        double angleDegrees, string axis, Point3d pivot)
    {
        string name = GetBestName(comp);
        lw.WriteLine("  Rotating: " + name);
        lw.WriteLine("  Angle: " + angleDegrees + " deg  Axis: " + axis);

        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        // Rotation matrix (incremental, around chosen world axis)
        Matrix3x3 dR = new Matrix3x3();
        switch (axis)
        {
            case "X":
                dR.Xx = 1;    dR.Xy = 0;    dR.Xz = 0;
                dR.Yx = 0;    dR.Yy = cos;  dR.Yz = -sin;
                dR.Zx = 0;    dR.Zy = sin;  dR.Zz = cos;
                break;
            case "Y":
                dR.Xx = cos;  dR.Xy = 0;    dR.Xz = sin;
                dR.Yx = 0;    dR.Yy = 1;    dR.Yz = 0;
                dR.Zx = -sin; dR.Zy = 0;    dR.Zz = cos;
                break;
            default: // Z
                dR.Xx = cos;  dR.Xy = -sin; dR.Xz = 0;
                dR.Yx = sin;  dR.Yy = cos;  dR.Yz = 0;
                dR.Zx = 0;    dR.Zy = 0;    dR.Zz = 1;
                break;
        }

        // MoveComponent maps every world-space point p  →  dR*p + delta.
        // To keep 'pivot' fixed:  dR*pivot + delta = pivot  →  delta = pivot - dR*pivot
        double px = pivot.X, py = pivot.Y, pz = pivot.Z;
        double rpx = dR.Xx*px + dR.Xy*py + dR.Xz*pz;
        double rpy = dR.Yx*px + dR.Yy*py + dR.Yz*pz;
        double rpz = dR.Zx*px + dR.Zy*py + dR.Zz*pz;
        Vector3d delta = new Vector3d(px - rpx, py - rpy, pz - rpz);

        lw.WriteLine("  Compensation delta: (" +
            delta.X.ToString("F4") + ", " +
            delta.Y.ToString("F4") + ", " +
            delta.Z.ToString("F4") + ")");

        // Suppress constraints so they cannot fight the rotation
        List<NXOpen.Positioning.Constraint> suppressed = SuppressAllConstraints(workPart);
        lw.WriteLine("  Suppressed " + suppressed.Count + " constraint(s).");

        // Apply the move
        bool success = false;
        try
        {
            workPart.ComponentAssembly.MoveComponent(comp, delta, dR);
            lw.WriteLine("  OK: Rotation applied.");
            success = true;
        }
        catch (Exception ex)
        {
            lw.WriteLine("  ERROR: " + ex.Message);
            lw.WriteLine("  Manual fix: Assembly Navigator -> right-click component");
            lw.WriteLine("  -> 'Assembly Constraints' -> suppress all, then retry.");
        }

        // Restore constraints
        RestoreConstraints(suppressed);
        if (success && suppressed.Count > 0)
            lw.WriteLine("  Constraints restored.");

        lw.WriteLine("");
    }


    // ─── Selection dialog ─────────────────────────────────────────────────────

    static bool ShowSelectionDialog(List<CompInfo> allComps,
        int autoBody, int autoCover, int autoOutlet,
        out int bodyIdx, out int coverIdx, out int outletIdx,
        out double coverAngle, out double outletAngle,
        out string rotAxis, out Point3d pivot)
    {
        bodyIdx = 0; coverIdx = 0; outletIdx = 0;
        coverAngle = 0; outletAngle = 0; rotAxis = "Z";
        pivot = new Point3d(0, 0, 0);

        Form form = new Form();
        form.Text = "Assembly Component Rotator v3";
        form.Width = 620;
        form.Height = 680;   // taller to fit pivot fields
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

        // Rotation axis
        AddLabel(form, "Rotation Axis:", new Font("Segoe UI", 9, FontStyle.Bold),
            Color.Black, labelX, y, 120);
        RadioButton radioZ = AddRadio(form, "Z-Axis (vertical)", true,  140, y, 145);
        RadioButton radioX = AddRadio(form, "X-Axis",           false, 290, y, 100);
        RadioButton radioY = AddRadio(form, "Y-Axis",           false, 400, y, 100);
        y += 35;

        // Pivot point
        Panel sep3 = new Panel();
        sep3.BackColor = Color.FromArgb(220, 220, 220);
        sep3.Left = labelX; sep3.Top = y; sep3.Width = comboW; sep3.Height = 1;
        form.Controls.Add(sep3); y += 12;

        AddLabel(form, "Rotation Axis Point  (a point that lies on the axis – usually body origin):",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(80, 0, 120), labelX, y, 540);
        y += 20;

        AddLabel(form, "X:", form.Font, Color.Black, labelX,      y + 3, 20);
        TextBox pivotXBox = AddTextBox(form, "0", labelX + 20,    y, 70);
        AddLabel(form, "Y:", form.Font, Color.Black, labelX + 100, y + 3, 20);
        TextBox pivotYBox = AddTextBox(form, "0", labelX + 120,   y, 70);
        AddLabel(form, "Z:", form.Font, Color.Black, labelX + 200, y + 3, 20);
        TextBox pivotZBox = AddTextBox(form, "0", labelX + 220,   y, 70);

        Label pivotHint = new Label();
        pivotHint.Text = "Tip: open NX Information -> Object to read the body origin coordinates.";
        pivotHint.ForeColor = Color.Gray;
        pivotHint.Left = labelX; pivotHint.Top = y + 26; pivotHint.Width = 540;
        form.Controls.Add(pivotHint);
        y += 55;

        // Buttons
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

        double pivX = 0, pivY = 0, pivZ = 0;
        double.TryParse(pivotXBox.Text, out pivX);
        double.TryParse(pivotYBox.Text, out pivY);
        double.TryParse(pivotZBox.Text, out pivZ);
        pivot = new Point3d(pivX, pivY, pivZ);

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
