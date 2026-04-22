// NX Open C# Macro - Assembly Component Rotator (v3)
//
// Why previous versions failed
// ────────────────────────────
// 1. Outlet not rotating / cover drifting in Z
//    Root cause: constraints were RESTORED after the move, so NX's assembly
//    update cycle immediately re-ran the solver and snapped everything back.
//    For Z rotation, the solver was also pushing the cover upward to satisfy
//    distance constraints that it no longer could satisfy after the XY move.
//
// 2. Outlet fully blocked
//    Root cause: constraints were still active and the solver reverted the move
//    because it could not satisfy them in the new position.
//
// Fix summary
// ───────────
// * Constraints are suppressed (NXOpen.Positioning.Constraint.Suppressed = true)
//   and LEFT suppressed after rotation so the new position holds.
//   A dialog checkbox lets the user restore them if needed.
// * MoveComponent is used with pivot-compensation delta (pivot - R*pivot)
//   so the component rotates around the specified pivot point.
// * Auto-pivot is read from the body's origin via AskComponentData.
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
            MessageBox.Show("No assembly is open.",
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

        // Print tree
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

        // Auto-detect body / cover / outlet by name
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
            defaultPivot = TryGetComponentOrigin(allComps[autoBody].Comp);
            lw.WriteLine("Auto-pivot (body origin): (" +
                defaultPivot.X.ToString("F3") + ", " +
                defaultPivot.Y.ToString("F3") + ", " +
                defaultPivot.Z.ToString("F3") + ")");
            lw.WriteLine("  (If this looks wrong, override X/Y/Z in the dialog)");
            lw.WriteLine("");
        }

        int bodyIdx, coverIdx, outletIdx;
        double coverAngle, outletAngle;
        string rotAxis;
        Point3d pivot;
        bool restoreConstraints;

        bool ok = ShowSelectionDialog(allComps, autoBody, autoCover, autoOutlet,
            defaultPivot,
            out bodyIdx, out coverIdx, out outletIdx,
            out coverAngle, out outletAngle, out rotAxis, out pivot,
            out restoreConstraints);

        if (!ok) { lw.WriteLine("Cancelled by user."); return; }

        lw.WriteLine("Selection:");
        lw.WriteLine("  Body   (FIXED): " + allComps[bodyIdx].FullName);
        lw.WriteLine("  Cover:          " + allComps[coverIdx].FullName  + "  ->  " + coverAngle  + " deg");
        lw.WriteLine("  Outlet:         " + allComps[outletIdx].FullName + "  ->  " + outletAngle + " deg");
        lw.WriteLine("  Axis:           " + rotAxis);
        lw.WriteLine("  Pivot:          (" + pivot.X + ", " + pivot.Y + ", " + pivot.Z + ")");
        lw.WriteLine("  Restore constraints after: " + restoreConstraints);
        lw.WriteLine("");

        if (coverAngle  != 0)
            RotateComponent(workPart, allComps[coverIdx].Comp,  coverAngle,  rotAxis, pivot, restoreConstraints);
        else
            lw.WriteLine("  Cover:  skipped (0 deg)");

        if (outletAngle != 0)
            RotateComponent(workPart, allComps[outletIdx].Comp, outletAngle, rotAxis, pivot, restoreConstraints);
        else
            lw.WriteLine("  Outlet: skipped (0 deg)");

        lw.WriteLine("");
        lw.WriteLine("=== Done! ===");
        if (!restoreConstraints)
            lw.WriteLine("NOTE: Constraints were suppressed and left suppressed to hold");
        lw.WriteLine("      the new position. To re-constrain, use Assembly Navigator");
        lw.WriteLine("      -> right-click constraint -> Unsuppress.");
    }


    // ─── Component origin helper ──────────────────────────────────────────────
    // UF_ASSEM_ask_absocc_transform is not wrapped in C# for all NX versions.
    // AskComponentData (6-arg form) is the reliable fallback.

    static Point3d TryGetComponentOrigin(Component comp)
    {
        try
        {
            string partName, refSetName;
            int layer;
            double[] origin = new double[3];
            double[] csys   = new double[9];
            UFSession.GetUFSession().Assem.AskComponentData(
                comp.Tag, out partName, out refSetName, out layer, origin, csys);
            return new Point3d(origin[0], origin[1], origin[2]);
        }
        catch { }
        return new Point3d(0, 0, 0);
    }

    // 3×3 rotation matrix as double[row, col]
    static double[,] BuildDR(double angleDeg, string axis)
    {
        double a = angleDeg * Math.PI / 180.0;
        double c = Math.Cos(a), s = Math.Sin(a);
        double[,] dR = new double[3, 3];
        switch (axis)
        {
            case "X":
                dR[0,0]=1; dR[0,1]=0;  dR[0,2]=0;
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

    static Matrix3x3 ToNXMatrix(double[,] dR)
    {
        Matrix3x3 m = new Matrix3x3();
        m.Xx = dR[0,0]; m.Xy = dR[0,1]; m.Xz = dR[0,2];
        m.Yx = dR[1,0]; m.Yy = dR[1,1]; m.Yz = dR[1,2];
        m.Zx = dR[2,0]; m.Zy = dR[2,1]; m.Zz = dR[2,2];
        return m;
    }


    // ─── Constraint suppression ───────────────────────────────────────────────
    // UFObj.AskSuppression / SetSuppression are not available in all NX C# builds.
    // We rely solely on NXOpen.Positioning.Constraint.Suppressed (NX9+).

    static List<NXOpen.Positioning.Constraint> SuppressAllConstraints(Part workPart)
    {
        var suppressed = new List<NXOpen.Positioning.Constraint>();
        int scanned = 0;
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            Tag tag = Tag.Null;
            ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref tag);
            while (tag != Tag.Null)
            {
                scanned++;
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
            lw.WriteLine("  Warning: CycleObjsInPart failed: " + ex.Message);
        }
        lw.WriteLine("  Objects scanned: " + scanned +
                     "  |  Suppressed: " + suppressed.Count);
        return suppressed;
    }

    static void RestoreConstraints(List<NXOpen.Positioning.Constraint> constraints)
    {
        foreach (NXOpen.Positioning.Constraint c in constraints)
        {
            try { c.Suppressed = false; } catch { }
        }
    }


    // ─── Core rotation ────────────────────────────────────────────────────────

    static void RotateComponent(Part workPart, Component comp,
        double angleDeg, string axis, Point3d pivot, bool restoreAfter)
    {
        string name = GetBestName(comp);
        lw.WriteLine("─── " + name + " ───");
        lw.WriteLine("  Angle: " + angleDeg + " deg   Axis: " + axis);
        lw.WriteLine("  Pivot: (" + pivot.X.ToString("F3") + ", " +
                      pivot.Y.ToString("F3") + ", " + pivot.Z.ToString("F3") + ")");

        double[,] dR = BuildDR(angleDeg, axis);

        // delta = pivot - R*pivot  →  MoveComponent rotates the component
        // around the given pivot point rather than around the world origin.
        double px = pivot.X, py = pivot.Y, pz = pivot.Z;
        Vector3d delta = new Vector3d(
            px - (dR[0,0]*px + dR[0,1]*py + dR[0,2]*pz),
            py - (dR[1,0]*px + dR[1,1]*py + dR[1,2]*pz),
            pz - (dR[2,0]*px + dR[2,1]*py + dR[2,2]*pz));

        // Step 1: suppress constraints so the solver cannot revert the move.
        // Constraints are left suppressed by default (controlled by dialog checkbox).
        var suppressed = SuppressAllConstraints(workPart);

        // Step 2: apply the incremental rotation around the pivot.
        try
        {
            workPart.ComponentAssembly.MoveComponent(comp, delta, ToNXMatrix(dR));
            lw.WriteLine("  MoveComponent: OK");
        }
        catch (Exception ex)
        {
            lw.WriteLine("  MoveComponent failed: " + ex.Message);
        }

        // Step 3: optionally restore constraints.
        if (restoreAfter && suppressed.Count > 0)
        {
            RestoreConstraints(suppressed);
            lw.WriteLine("  Constraints RESTORED (new position may revert on next update).");
        }
        else if (suppressed.Count > 0)
        {
            lw.WriteLine("  Constraints left SUPPRESSED to hold the new position.");
        }

        lw.WriteLine("");
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
                    string db = attr.StringValue;
                    if (!string.IsNullOrEmpty(db) && !best.ToUpper().Contains(db.ToUpper()))
                        best = best + "  (" + db + ")";
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


    // ─── Selection dialog ─────────────────────────────────────────────────────

    static bool ShowSelectionDialog(List<CompInfo> allComps,
        int autoBody, int autoCover, int autoOutlet,
        Point3d defaultPivot,
        out int bodyIdx, out int coverIdx, out int outletIdx,
        out double coverAngle, out double outletAngle,
        out string rotAxis, out Point3d pivot,
        out bool restoreConstraints)
    {
        bodyIdx = 0; coverIdx = 0; outletIdx = 0;
        coverAngle = 0; outletAngle = 0; rotAxis = "Z";
        pivot = defaultPivot; restoreConstraints = false;

        Form form = new Form();
        form.Text = "Assembly Component Rotator v3";
        form.Width = 630; form.Height = 730;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = true;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false; form.MinimizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Segoe UI", 9);

        int y = 12;
        const int lx = 15, cw = 578;

        // Header
        FL(form, "ASSEMBLY COMPONENT ROTATOR  v3",
            new Font("Segoe UI", 11, FontStyle.Bold), Color.FromArgb(0,90,158), lx, y, 550); y += 28;
        FL(form, "Constraints are suppressed before rotating and LEFT suppressed to hold position.",
            form.Font, Color.Gray, lx, y, 570); y += 28;
        HS(form, lx, y, cw); y += 12;

        // BODY
        FL(form, "BODY  (fixed – will NOT move)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,100,200), lx, y, 400); y += 20;
        ComboBox bodyCombo = FC(form, allComps, lx, y, cw,
            autoBody >= 0 ? autoBody : 0); y += 35;

        // COVER
        FL(form, "COVER  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(180,0,0), lx, y, 300); y += 20;
        ComboBox coverCombo = FC(form, allComps, lx, y, cw,
            autoCover >= 0 ? autoCover : Math.Min(1, allComps.Count-1)); y += 28;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 110);
        TextBox coverAngleBox = FT(form, "0", lx+115, y, 80); y += 38;

        // OUTLET
        FL(form, "OUTLET  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,130,0), lx, y, 300); y += 20;
        ComboBox outletCombo = FC(form, allComps, lx, y, cw,
            autoOutlet >= 0 ? autoOutlet : Math.Min(2, allComps.Count-1)); y += 28;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 110);
        TextBox outletAngleBox = FT(form, "0", lx+115, y, 80); y += 38;

        HS(form, lx, y, cw); y += 12;

        // Axis
        FL(form, "Rotation Axis:", new Font("Segoe UI", 9, FontStyle.Bold), Color.Black, lx, y, 120);
        RadioButton radioZ = FR(form, "Z-Axis (vertical)", true,  140, y, 145);
        RadioButton radioX = FR(form, "X-Axis",           false, 290, y, 100);
        RadioButton radioY = FR(form, "Y-Axis",           false, 400, y, 100);
        y += 36;

        HS(form, lx, y, cw); y += 12;

        // Pivot
        FL(form, "Rotation Axis Point  (auto-detected from body – override if wrong):",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(80,0,120), lx, y, 560); y += 22;
        FL(form, "X:", form.Font, Color.Black, lx,      y+3, 18);
        TextBox pivX = FT(form, defaultPivot.X.ToString("F3"), lx+18,   y, 72);
        FL(form, "Y:", form.Font, Color.Black, lx+100,  y+3, 18);
        TextBox pivY = FT(form, defaultPivot.Y.ToString("F3"), lx+118,  y, 72);
        FL(form, "Z:", form.Font, Color.Black, lx+200,  y+3, 18);
        TextBox pivZ = FT(form, defaultPivot.Z.ToString("F3"), lx+218,  y, 72);
        y += 30;
        FL(form, "Tip: see Listing Window for col-major vs row-major origin values to verify.",
            form.Font, Color.Gray, lx, y, 560); y += 24;

        HS(form, lx, y, cw); y += 12;

        // Restore checkbox
        CheckBox restoreChk = new CheckBox();
        restoreChk.Text = "Restore constraints after rotation  " +
                          "(WARNING: new position may revert)";
        restoreChk.Left = lx; restoreChk.Top = y;
        restoreChk.Width = 540; restoreChk.Checked = false;
        restoreChk.ForeColor = Color.DarkRed;
        form.Controls.Add(restoreChk); y += 32;

        // Buttons
        Button okBtn = new Button();
        okBtn.Text = "Apply Rotation";
        okBtn.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        okBtn.BackColor = Color.FromArgb(0,120,215); okBtn.ForeColor = Color.White;
        okBtn.FlatStyle = FlatStyle.Flat; okBtn.FlatAppearance.BorderSize = 0;
        okBtn.Left = lx; okBtn.Top = y; okBtn.Width = 280; okBtn.Height = 42;
        okBtn.DialogResult = DialogResult.OK;
        form.Controls.Add(okBtn);

        Button cancelBtn = new Button();
        cancelBtn.Text = "Cancel";
        cancelBtn.Font = new Font("Segoe UI", 10);
        cancelBtn.FlatStyle = FlatStyle.Flat;
        cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(180,180,180);
        cancelBtn.Left = 313; cancelBtn.Top = y; cancelBtn.Width = 280; cancelBtn.Height = 42;
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
        double px2 = 0, py2 = 0, pz2 = 0;
        double.TryParse(pivX.Text, out px2);
        double.TryParse(pivY.Text, out py2);
        double.TryParse(pivZ.Text, out pz2);
        pivot = new Point3d(px2, py2, pz2);
        restoreConstraints = restoreChk.Checked;

        form.Dispose();
        return true;
    }


    // ─── Dialog control factory helpers ──────────────────────────────────────

    static void HS(Form f, int x, int y, int w)
    {
        f.Controls.Add(new Panel { BackColor=Color.FromArgb(220,220,220),
                                   Left=x, Top=y, Width=w, Height=1 });
    }

    static void FL(Form f, string t, Font font, Color color, int x, int y, int w)
    {
        f.Controls.Add(new Label { Text=t, Font=font, ForeColor=color,
                                   Left=x, Top=y, Width=w });
    }

    static ComboBox FC(Form f, List<CompInfo> items, int x, int y, int w, int sel)
    {
        ComboBox cb = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,
            Left=x, Top=y, Width=w, DropDownWidth=720, Font=new Font("Consolas",9) };
        foreach (CompInfo ci in items) cb.Items.Add(ci.TreeDisplay);
        cb.SelectedIndex = Math.Max(0, Math.Min(sel, items.Count-1));
        f.Controls.Add(cb);
        return cb;
    }

    static TextBox FT(Form f, string text, int x, int y, int w)
    {
        TextBox tb = new TextBox { Text=text, Left=x, Top=y, Width=w,
                                   TextAlign=HorizontalAlignment.Center };
        f.Controls.Add(tb);
        return tb;
    }

    static RadioButton FR(Form f, string text, bool check, int x, int y, int w)
    {
        RadioButton r = new RadioButton { Text=text, Checked=check,
                                          Left=x, Top=y, Width=w };
        f.Controls.Add(r);
        return r;
    }
}
