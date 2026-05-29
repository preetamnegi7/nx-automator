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
// * Auto-pivot is read from the body's origin via Component.GetPosition (managed API).
//
// Interactive session (v3.1)
// ──────────────────────────
// * "Apply Rotation" no longer closes the macro. The dialog stays open so you
//   can apply many rotations in one session.
// * Every rotation is recorded as an operation in a Change Log shown live in the
//   dialog, plus a message block pops up after each Apply summarising what changed.
// * Undo / Redo buttons walk an operation stack and re-apply the inverse /
//   original rotation so positions can be stepped back and forward.
// * The only thing that ends the session is the Close button.
//
// v3.2 changes
// ────────────
// * Single log only: the NX Listing/Information window is no longer used; the
//   in-dialog Change Log is the one and only log.
// * Vac valve follows the cover: the part mounted in the cover's port (auto
//   detected by name, shown in its own selector) is rotated together with the
//   cover using the same angle/axis/pivot, so it no longer stays behind.
// * Cover step angle: the cover's rotation must be a whole multiple of a
//   user-supplied step angle (its indexed positions), so it cannot be driven to
//   a position that is not physically feasible.
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

    // The single change log. All messages go here (no NX Listing Window).
    static TextBox _logBox;

    class CompInfo
    {
        public Component Comp;
        public string FullName;
        public string ShortName;
        public int Level;
        public string TreeDisplay;
    }

    // One row of the Bill of Materials: a unique part and every instance of it.
    class BomItem
    {
        public string PartName;
        public int Quantity;
        public int MinLevel = int.MaxValue;
        public List<Component> Instances = new List<Component>();
    }

    // One applied rotation, kept on the undo / redo stacks so it can be
    // replayed forwards (redo) or inverted (undo).
    class RotationOp
    {
        public Component Comp;
        public string CompName;
        public double AngleDeg;
        public string Axis;
        public Point3d Pivot;
        public string Description;
    }

    public static void Main(string[] args)
    {
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

        // Auto-detect body / cover / outlet / vac-valve by name
        int autoBody = -1, autoCover = -1, autoOutlet = -1, autoVac = -1;
        for (int i = 0; i < allComps.Count; i++)
        {
            string up = allComps[i].FullName.ToUpper();
            if (autoBody   < 0 && up.Contains("BODY")   && !up.Contains("COVER")) autoBody   = i;
            if (autoCover  < 0 && up.Contains("COVER"))                             autoCover  = i;
            if (autoOutlet < 0 && up.Contains("OUTLET"))                            autoOutlet = i;
            if (autoVac    < 0 && (up.Contains("VAC") || up.Contains("VALVE")))     autoVac    = i;
        }

        // Auto-read body origin to pre-fill pivot
        Point3d defaultPivot = new Point3d(0, 0, 0);
        if (autoBody >= 0)
            defaultPivot = TryGetComponentOrigin(allComps[autoBody].Comp);

        // Single interactive dialog drives the whole session: apply, undo,
        // redo and the change log all live inside it. It only returns when
        // the user clicks Close.
        RunRotatorDialog(workPart, allComps, autoBody, autoCover, autoOutlet, autoVac, defaultPivot);
    }


    // ─── Component origin helper ──────────────────────────────────────────────
    // Component.GetPosition is the managed NX Open C# API that returns the
    // component's world-space origin and orientation directly (no UF needed).

    static Point3d TryGetComponentOrigin(Component comp)
    {
        try
        {
            Point3d origin;
            Matrix3x3 orientation;
            comp.GetPosition(out origin, out orientation);
            return origin;
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

    // delta = pivot - R*pivot  →  MoveComponent rotates the component around the
    // given pivot point rather than around the world origin.
    static Vector3d ComputeDelta(double[,] dR, Point3d pivot)
    {
        double px = pivot.X, py = pivot.Y, pz = pivot.Z;
        return new Vector3d(
            px - (dR[0,0]*px + dR[0,1]*py + dR[0,2]*pz),
            py - (dR[1,0]*px + dR[1,1]*py + dR[1,2]*pz),
            pz - (dR[2,0]*px + dR[2,1]*py + dR[2,2]*pz));
    }


    // ─── Constraint suppression ───────────────────────────────────────────────
    // UFObj.AskSuppression / SetSuppression are not available in all NX C# builds.
    // We rely solely on NXOpen.Positioning.Constraint.Suppressed (NX9+).

    static List<NXOpen.Positioning.Constraint> SuppressAllConstraints(Part workPart)
    {
        var suppressed = new List<NXOpen.Positioning.Constraint>();
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            Tag tag = Tag.Null;
            ufs.Obj.CycleObjsInPart(workPart.Tag, -1, ref tag);
            while (tag != Tag.Null)
            {
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
            AppendLog("Warning: constraint scan failed: " + ex.Message);
        }
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

    // Apply one rotation, build the RotationOp record and return it. Constraints
    // are suppressed first so the solver cannot revert the move, and are only
    // restored if the caller asked for it (which may let the position revert).
    static RotationOp ApplyOneRotation(Part workPart, Component comp,
        double angleDeg, string axis, Point3d pivot, bool restoreAfter)
    {
        var suppressed = SuppressAllConstraints(workPart);
        DoMove(workPart, comp, angleDeg, axis, pivot, false);

        if (restoreAfter && suppressed.Count > 0)
        {
            RestoreConstraints(suppressed);
            AppendLog("Constraints RESTORED (position may revert on next update).");
        }

        RotationOp op = new RotationOp();
        op.Comp = comp;
        op.CompName = GetBestName(comp);
        op.AngleDeg = angleDeg;
        op.Axis = axis;
        op.Pivot = pivot;
        op.Description = op.CompName + "   " + angleDeg + " deg about " + axis +
            "   pivot(" + pivot.X.ToString("F1") + ", " +
            pivot.Y.ToString("F1") + ", " + pivot.Z.ToString("F1") + ")";
        return op;
    }

    // Pure incremental move around the pivot. suppressFirst lets undo/redo make
    // sure constraints are out of the way before stepping the position.
    static void DoMove(Part workPart, Component comp,
        double angleDeg, string axis, Point3d pivot, bool suppressFirst)
    {
        if (suppressFirst) SuppressAllConstraints(workPart);
        double[,] dR = BuildDR(angleDeg, axis);
        Vector3d delta = ComputeDelta(dR, pivot);
        try
        {
            workPart.ComponentAssembly.MoveComponent(comp, delta, ToNXMatrix(dR));
        }
        catch (Exception ex)
        {
            AppendLog("MoveComponent failed: " + ex.Message);
            MessageBox.Show("MoveComponent failed: " + ex.Message,
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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


    // ─── Interactive rotator dialog ───────────────────────────────────────────
    // Stays open for the whole session. Apply rotates in place and logs; Undo /
    // Redo step the operation stack; Close ends the session.

    static void RunRotatorDialog(Part workPart, List<CompInfo> allComps,
        int autoBody, int autoCover, int autoOutlet, int autoVac, Point3d defaultPivot)
    {
        var history = new List<RotationOp>();   // applied ops (undo stack)
        var redo    = new List<RotationOp>();   // undone ops (redo stack)

        Form form = new Form();
        form.Text = "Assembly Component Rotator v3";
        form.Width = 650; form.Height = 760;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = true;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false; form.MinimizeBox = false;
        form.BackColor = Color.White;
        form.Font = new Font("Segoe UI", 9);

        int y = 12;
        const int lx = 15, cw = 598;

        // Header
        FL(form, "ASSEMBLY COMPONENT ROTATOR  v3",
            new Font("Segoe UI", 11, FontStyle.Bold), Color.FromArgb(0,90,158), lx, y, 420);

        // BOM button – the BOM stays closed and only opens when this is clicked.
        Button bomBtn = new Button();
        bomBtn.Text = "Show BOM";
        bomBtn.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        bomBtn.Left = 475; bomBtn.Top = y - 2; bomBtn.Width = 140; bomBtn.Height = 28;
        bomBtn.FlatStyle = FlatStyle.Flat;
        bomBtn.BackColor = Color.FromArgb(0,120,215); bomBtn.ForeColor = Color.White;
        bomBtn.FlatAppearance.BorderSize = 0;
        bomBtn.Click += delegate { ShowBomDialog(allComps); };
        form.Controls.Add(bomBtn);
        bomBtn.BringToFront();
        y += 28;
        FL(form, "Constraints are suppressed before rotating and LEFT suppressed to hold position.",
            form.Font, Color.Gray, lx, y, 590); y += 24;
        HS(form, lx, y, cw); y += 10;

        // BODY
        FL(form, "BODY  (fixed – will NOT move)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,100,200), lx, y, 400); y += 20;
        ComboBox bodyCombo = FC(form, allComps, lx, y, cw,
            autoBody >= 0 ? autoBody : 0); y += 30;

        // COVER  + step angle
        FL(form, "COVER  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(180,0,0), lx, y, 300); y += 20;
        ComboBox coverCombo = FC(form, allComps, lx, y, cw,
            autoCover >= 0 ? autoCover : Math.Min(1, allComps.Count-1)); y += 26;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 100);
        TextBox coverAngleBox = FT(form, "0", lx+105, y, 70);
        FL(form, "Step angle (deg):", form.Font, Color.Black, lx+200, y+3, 110);
        TextBox stepBox = FT(form, "", lx+315, y, 70); y += 24;
        FL(form, "Cover angle must be a whole multiple of the step angle (its indexed positions).",
            form.Font, Color.Gray, lx, y, 590); y += 26;

        // VAC VALVE (rotates with cover)
        FL(form, "VAC VALVE  (rotates together with COVER)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(120,80,0), lx, y, 420); y += 20;
        ComboBox vacCombo = FC(form, allComps, lx, y, cw,
            autoVac >= 0 ? autoVac : 0); y += 26;
        CheckBox vacChk = new CheckBox();
        vacChk.Text = "Rotate this part together with the cover (same angle / axis / pivot)";
        vacChk.Left = lx; vacChk.Top = y; vacChk.Width = 590;
        vacChk.Checked = (autoVac >= 0);
        form.Controls.Add(vacChk); y += 28;

        // OUTLET
        FL(form, "OUTLET  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,130,0), lx, y, 300); y += 20;
        ComboBox outletCombo = FC(form, allComps, lx, y, cw,
            autoOutlet >= 0 ? autoOutlet : Math.Min(2, allComps.Count-1)); y += 26;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 100);
        TextBox outletAngleBox = FT(form, "0", lx+105, y, 70); y += 32;

        HS(form, lx, y, cw); y += 10;

        // Axis
        FL(form, "Rotation Axis:", new Font("Segoe UI", 9, FontStyle.Bold), Color.Black, lx, y, 120);
        RadioButton radioZ = FR(form, "Z-Axis (vertical)", true,  140, y, 145);
        RadioButton radioX = FR(form, "X-Axis",           false, 290, y, 100);
        RadioButton radioY = FR(form, "Y-Axis",           false, 400, y, 100);
        y += 32;

        // Pivot
        FL(form, "Rotation Axis Point  (auto-detected from body – override if wrong):",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(80,0,120), lx, y, 580); y += 22;
        FL(form, "X:", form.Font, Color.Black, lx,      y+3, 18);
        TextBox pivX = FT(form, defaultPivot.X.ToString("F3"), lx+18,   y, 72);
        FL(form, "Y:", form.Font, Color.Black, lx+100,  y+3, 18);
        TextBox pivY = FT(form, defaultPivot.Y.ToString("F3"), lx+118,  y, 72);
        FL(form, "Z:", form.Font, Color.Black, lx+200,  y+3, 18);
        TextBox pivZ = FT(form, defaultPivot.Z.ToString("F3"), lx+218,  y, 72);
        y += 30;

        // Restore checkbox
        CheckBox restoreChk = new CheckBox();
        restoreChk.Text = "Restore constraints after each rotation  " +
                          "(WARNING: position may revert; breaks clean Undo)";
        restoreChk.Left = lx; restoreChk.Top = y;
        restoreChk.Width = 600; restoreChk.Checked = false;
        restoreChk.ForeColor = Color.DarkRed;
        form.Controls.Add(restoreChk); y += 30;

        HS(form, lx, y, cw); y += 10;

        // Action buttons row
        Button applyBtn = new Button();
        applyBtn.Text = "Apply Rotation";
        applyBtn.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        applyBtn.BackColor = Color.FromArgb(0,120,215); applyBtn.ForeColor = Color.White;
        applyBtn.FlatStyle = FlatStyle.Flat; applyBtn.FlatAppearance.BorderSize = 0;
        applyBtn.Left = lx; applyBtn.Top = y; applyBtn.Width = 210; applyBtn.Height = 40;
        form.Controls.Add(applyBtn);

        Button undoBtn = new Button();
        undoBtn.Text = "Undo";
        undoBtn.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        undoBtn.FlatStyle = FlatStyle.Flat;
        undoBtn.FlatAppearance.BorderColor = Color.FromArgb(180,180,180);
        undoBtn.BackColor = Color.FromArgb(245,245,245);
        undoBtn.Left = 235; undoBtn.Top = y; undoBtn.Width = 95; undoBtn.Height = 40;
        undoBtn.Enabled = false;
        form.Controls.Add(undoBtn);

        Button redoBtn = new Button();
        redoBtn.Text = "Redo";
        redoBtn.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        redoBtn.FlatStyle = FlatStyle.Flat;
        redoBtn.FlatAppearance.BorderColor = Color.FromArgb(180,180,180);
        redoBtn.BackColor = Color.FromArgb(245,245,245);
        redoBtn.Left = 338; redoBtn.Top = y; redoBtn.Width = 95; redoBtn.Height = 40;
        redoBtn.Enabled = false;
        form.Controls.Add(redoBtn);

        Button closeBtn = new Button();
        closeBtn.Text = "Close";
        closeBtn.Font = new Font("Segoe UI", 10);
        closeBtn.FlatStyle = FlatStyle.Flat;
        closeBtn.FlatAppearance.BorderColor = Color.FromArgb(180,180,180);
        closeBtn.Left = 505; closeBtn.Top = y; closeBtn.Width = 110; closeBtn.Height = 40;
        closeBtn.DialogResult = DialogResult.OK;
        form.Controls.Add(closeBtn);
        y += 48;

        // Change-log panel (the one and only log)
        FL(form, "Change Log:", new Font("Segoe UI", 9, FontStyle.Bold), Color.Black, lx, y, 200); y += 20;
        TextBox logBox = new TextBox();
        logBox.Multiline = true; logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 8.5f);
        logBox.BackColor = Color.FromArgb(250,250,250);
        logBox.Left = lx; logBox.Top = y; logBox.Width = cw; logBox.Height = 150;
        form.Controls.Add(logBox);
        y += 158;
        _logBox = logBox;

        form.Height = y + 50;
        form.AcceptButton = applyBtn;   // Enter applies (does not close)
        form.CancelButton = closeBtn;

        // ── Shared helpers (closures over the controls / stacks) ──
        Action refreshButtons = delegate
        {
            undoBtn.Enabled = history.Count > 0;
            redoBtn.Enabled = redo.Count > 0;
        };

        applyBtn.Click += delegate
        {
            string axis = radioX.Checked ? "X" : (radioY.Checked ? "Y" : "Z");

            double px2 = 0, py2 = 0, pz2 = 0;
            double.TryParse(pivX.Text, out px2);
            double.TryParse(pivY.Text, out py2);
            double.TryParse(pivZ.Text, out pz2);
            Point3d pivot = new Point3d(px2, py2, pz2);

            double coverAngle, outletAngle, step;
            if (!double.TryParse(coverAngleBox.Text,  out coverAngle))  coverAngle  = 0;
            if (!double.TryParse(outletAngleBox.Text, out outletAngle)) outletAngle = 0;
            if (!double.TryParse(stepBox.Text,        out step))        step        = 0;

            bool restore = restoreChk.Checked;

            // Cover step-angle enforcement: the cover only has discrete indexed
            // positions, so its rotation must be a whole multiple of the step.
            if (coverAngle != 0)
            {
                if (step <= 0)
                {
                    MessageBox.Show(
                        "Enter the COVER step angle first.\n\n" +
                        "The cover has indexed / stepped positions, so its rotation must be a " +
                        "whole multiple of the step angle. This prevents moving it to a position " +
                        "that is not physically feasible.",
                        "Step angle required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                double ratio = coverAngle / step;
                double nearest = Math.Round(ratio);
                if (Math.Abs(coverAngle - nearest * step) > 0.001)
                {
                    double low  = Math.Floor(ratio)   * step;
                    double high = Math.Ceiling(ratio)  * step;
                    MessageBox.Show(
                        "Cover angle " + coverAngle + "° is not a multiple of the step angle " +
                        step + "°.\n\nNearest feasible values: " + low + "° or " + high + "°.",
                        "Invalid cover angle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var applied = new List<string>();

            if (coverAngle != 0)
            {
                int coverIdx = coverCombo.SelectedIndex;
                RotationOp op = ApplyOneRotation(workPart,
                    allComps[coverIdx].Comp, coverAngle, axis, pivot, restore);
                history.Add(op); applied.Add(op.Description);
                AppendLog("APPLY  " + op.Description);

                // Vac valve follows the cover: same angle / axis / pivot.
                int vacIdx = vacCombo.SelectedIndex;
                if (vacChk.Checked && vacIdx >= 0 && vacIdx != coverIdx)
                {
                    RotationOp vop = ApplyOneRotation(workPart,
                        allComps[vacIdx].Comp, coverAngle, axis, pivot, restore);
                    history.Add(vop); applied.Add(vop.Description + "  [with cover]");
                    AppendLog("APPLY  " + vop.Description + "  [with cover]");
                }
            }

            if (outletAngle != 0)
            {
                RotationOp op = ApplyOneRotation(workPart,
                    allComps[outletCombo.SelectedIndex].Comp, outletAngle, axis, pivot, restore);
                history.Add(op); applied.Add(op.Description);
                AppendLog("APPLY  " + op.Description);
            }

            if (applied.Count == 0)
            {
                MessageBox.Show("Both angles are 0 — nothing to rotate.",
                    "Nothing applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            redo.Clear();           // a fresh action invalidates the redo branch
            refreshButtons();

            // Pop-up message block summarising what just changed.
            string msg = "Applied " + applied.Count + " rotation(s):\n\n  " +
                         string.Join("\n  ", applied.ToArray()) +
                         "\n\nTotal operations this session: " + history.Count;
            MessageBox.Show(msg, "Rotation Applied",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        undoBtn.Click += delegate
        {
            if (history.Count == 0) return;
            RotationOp op = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            DoMove(workPart, op.Comp, -op.AngleDeg, op.Axis, op.Pivot, true);
            redo.Add(op);
            AppendLog("UNDO   " + op.Description);
            refreshButtons();
        };

        redoBtn.Click += delegate
        {
            if (redo.Count == 0) return;
            RotationOp op = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            DoMove(workPart, op.Comp, op.AngleDeg, op.Axis, op.Pivot, true);
            history.Add(op);
            AppendLog("REDO   " + op.Description);
            refreshButtons();
        };

        refreshButtons();
        AppendLog("Ready. Set angles and click Apply Rotation.");

        form.ShowDialog();
        AppendLog("Session ended: " + history.Count + " net operation(s) applied.");
        _logBox = null;
        form.Dispose();
    }

    // Append one timestamped line to the single change-log panel.
    static void AppendLog(string line)
    {
        if (_logBox == null) return;
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText(stamp + "  " + line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }


    // ─── Bill of Materials (BOM) ──────────────────────────────────────────────
    // Closed by default – the BOM window only opens from the "Show BOM" button.
    // It lists unique parts with quantity + level, lets the user show/hide
    // (blank/unblank) components in the NX view, and export the BOM.

    // Grouping key: prefer the prototype part leaf name so identical parts merge.
    static string GetGroupKey(Component comp)
    {
        try
        {
            Part proto = comp.Prototype as Part;
            if (proto != null)
            {
                string leaf = proto.Leaf;
                if (!string.IsNullOrEmpty(leaf)) return leaf.ToUpper();
            }
        }
        catch { }
        return GetBestName(comp).ToUpper();
    }

    static List<BomItem> BuildBom(List<CompInfo> allComps)
    {
        var map = new Dictionary<string, BomItem>();
        var order = new List<string>();
        foreach (CompInfo ci in allComps)
        {
            string key = GetGroupKey(ci.Comp);
            BomItem item;
            if (!map.TryGetValue(key, out item))
            {
                item = new BomItem();
                item.PartName = ci.FullName;
                map[key] = item;
                order.Add(key);
            }
            item.Quantity++;
            item.Instances.Add(ci.Comp);
            if (ci.Level < item.MinLevel) item.MinLevel = ci.Level;
        }
        var list = new List<BomItem>();
        foreach (string k in order) list.Add(map[k]);
        return list;
    }

    static void ShowBomDialog(List<CompInfo> allComps)
    {
        List<BomItem> bom = BuildBom(allComps);

        Form f = new Form();
        f.Text = "Bill of Materials";
        f.Width = 650; f.Height = 580;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.TopMost = true;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false; f.MinimizeBox = false;
        f.BackColor = Color.White;
        f.Font = new Font("Segoe UI", 9);

        FL(f, "BILL OF MATERIALS",
            new Font("Segoe UI", 11, FontStyle.Bold), Color.FromArgb(0,90,158), 15, 12, 500);
        FL(f, "Select one or more rows, then Show / Hide to blank/unblank those components.",
            f.Font, Color.Gray, 15, 36, 600);

        ListView lv = new ListView();
        lv.Left = 15; lv.Top = 60; lv.Width = 605; lv.Height = 390;
        lv.View = System.Windows.Forms.View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = true;
        lv.MultiSelect = true;
        lv.HideSelection = false;
        lv.Font = new Font("Consolas", 9);
        lv.Columns.Add("#", 40);
        lv.Columns.Add("Part Name", 405);
        lv.Columns.Add("Qty", 60);
        lv.Columns.Add("Level", 80);

        int n = 1;
        foreach (BomItem bi in bom)
        {
            ListViewItem lvi = new ListViewItem(n.ToString());
            lvi.SubItems.Add(bi.PartName);
            lvi.SubItems.Add(bi.Quantity.ToString());
            lvi.SubItems.Add(bi.MinLevel == int.MaxValue ? "-" : bi.MinLevel.ToString());
            lvi.Tag = bi;
            lv.Items.Add(lvi);
            n++;
        }
        f.Controls.Add(lv);

        int by = 462;
        Button showSel = MkBtn(f, "Show Selected", 15,  by, 120);
        Button hideSel = MkBtn(f, "Hide Selected", 143, by, 120);
        Button showAll = MkBtn(f, "Show All",      271, by, 95);
        Button hideAll = MkBtn(f, "Hide All",      374, by, 95);
        Button export  = MkBtn(f, "Export...",     500, by, 120);
        Button close   = MkBtn(f, "Close",         500, by + 40, 120);

        showSel.Click += delegate { SetBomVisibility(GetSelectedBom(lv), true);  };
        hideSel.Click += delegate { SetBomVisibility(GetSelectedBom(lv), false); };
        showAll.Click += delegate { SetBomVisibility(bom, true);  };
        hideAll.Click += delegate { SetBomVisibility(bom, false); };
        export.Click  += delegate { ExportBom(bom); };
        close.Click   += delegate { f.Close(); };

        f.ShowDialog();
        f.Dispose();
    }

    static List<BomItem> GetSelectedBom(ListView lv)
    {
        var sel = new List<BomItem>();
        foreach (ListViewItem lvi in lv.SelectedItems)
        {
            BomItem bi = lvi.Tag as BomItem;
            if (bi != null) sel.Add(bi);
        }
        if (sel.Count == 0)
            MessageBox.Show("Select one or more BOM rows first.",
                "No selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return sel;
    }

    // Show = unblank, Hide = blank.  Uses UF SetBlankStatus on each component tag.
    static void SetBomVisibility(List<BomItem> items, bool show)
    {
        if (items == null || items.Count == 0) return;
        int cnt = 0;
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            int status = show ? UFConstants.UF_OBJ_NOT_BLANKED : UFConstants.UF_OBJ_BLANKED;
            foreach (BomItem bi in items)
                foreach (Component c in bi.Instances)
                {
                    try { ufs.Obj.SetBlankStatus(c.Tag, status); cnt++; }
                    catch { }
                }
        }
        catch (Exception ex)
        {
            AppendLog("BOM visibility error: " + ex.Message);
        }
        AppendLog("BOM " + (show ? "SHOW" : "HIDE") + ": updated " + cnt + " component(s).");
    }

    static void ExportBom(List<BomItem> bom)
    {
        string header = string.Format("{0,-4} {1,-48} {2,5} {3,6}", "#", "Part Name", "Qty", "Level");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Bill of Materials");
        sb.AppendLine(header);

        int n = 1, total = 0;
        foreach (BomItem bi in bom)
        {
            string lvl = bi.MinLevel == int.MaxValue ? "-" : bi.MinLevel.ToString();
            string line = string.Format("{0,-4} {1,-48} {2,5} {3,6}",
                n, Trunc(bi.PartName, 48), bi.Quantity, lvl);
            sb.AppendLine(line);
            total += bi.Quantity; n++;
        }
        string summary = "Unique parts: " + bom.Count + "   Total components: " + total;
        sb.AppendLine(summary);

        try
        {
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "Text file (*.txt)|*.txt|CSV file (*.csv)|*.csv";
            sfd.FileName = "BOM.txt";
            sfd.Title = "Save Bill of Materials";
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                string ext = System.IO.Path.GetExtension(sfd.FileName).ToLower();
                if (ext == ".csv")
                    System.IO.File.WriteAllText(sfd.FileName, BomToCsv(bom));
                else
                    System.IO.File.WriteAllText(sfd.FileName, sb.ToString());
                AppendLog("BOM saved to: " + sfd.FileName);
            }
            sfd.Dispose();
        }
        catch (Exception ex)
        {
            AppendLog("BOM file save failed: " + ex.Message);
        }

        MessageBox.Show(summary, "Bill of Materials",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static string BomToCsv(List<BomItem> bom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Index,Part Name,Quantity,Level");
        int n = 1;
        foreach (BomItem bi in bom)
        {
            string lvl = bi.MinLevel == int.MaxValue ? "" : bi.MinLevel.ToString();
            string nameCsv = "\"" + (bi.PartName ?? "").Replace("\"", "\"\"") + "\"";
            sb.AppendLine(n + "," + nameCsv + "," + bi.Quantity + "," + lvl);
            n++;
        }
        return sb.ToString();
    }

    static string Trunc(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n - 1) + "~";
    }

    static Button MkBtn(Form f, string text, int x, int y, int w)
    {
        Button b = new Button();
        b.Text = text; b.Left = x; b.Top = y; b.Width = w; b.Height = 32;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(180,180,180);
        b.BackColor = Color.FromArgb(245,245,245);
        f.Controls.Add(b);
        return b;
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
