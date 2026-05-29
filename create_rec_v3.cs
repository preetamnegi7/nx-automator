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
//   detected by name) is rotated together with the cover using the same
//   angle/axis/pivot, so it no longer stays behind.
// * Cover step angle: the cover's rotation must be a whole multiple of a
//   user-supplied step angle (its indexed positions), so it cannot be driven to
//   a position that is not physically feasible.
//
// v3.3 changes
// ────────────
// * Body reference lock: the body is captured at its home placement and forced
//   back to it after every operation, so it can never be dragged out of its
//   fixed reference position when the cover or outlet is rotated.
// * Outlet auto-detect no longer collides with the body subassembly (a name
//   like "Body Assembly - Straight Outlet" no longer auto-selects as outlet).
// * Vac valve is mandatory and no longer a configurable option: it is auto
//   detected and always rotates with the cover (shown read-only for reference).
//
// v3.4 changes  (fix "MoveComponent: Internal error: memory access violation")
// ───────────────────────────────────────────────────────────────────────────
// * Every MoveComponent is now bracketed by an undo mark and followed by an NX
//   update (MoveRaw). Updating after each move stops the internal-state
//   corruption that crashed NX when several moves ran back-to-back.
// * Constraints are suppressed ONCE per action instead of before every single
//   move, so we no longer re-cycle the whole part repeatedly.
// * A failed move now stops cleanly and is reported, instead of continuing.
//
// v3.5 changes
// ────────────
// * Outlet now has its own step angle, enforced exactly like the cover.
// * After a successful Apply, the cover and outlet angle fields reset to 0 so
//   the same rotation cannot be applied twice by accident (0 = already done).
// * The body lock can no longer crash the run: if it fails it is reported
//   quietly in the log and the rotation still stands (no error dialog).
// * The log now reports how many constraints were suppressed, so it is visible
//   whether suppression is actually holding the body in place.
//
// v3.6 changes  (fix outlet "memory access violation" — nested component)
// ───────────────────────────────────────────────────────────────────────────
// * Root cause: ComponentAssembly.MoveComponent only works on components it
//   owns DIRECTLY. The outlet is nested one level down (inside the body
//   subassembly), so moving it on the top assembly crashed NX.
// * Fix (MoveInContext): a nested component is now moved inside its own owning
//   subassembly via Component.DirectOwner. The component reference is mapped in
//   with ComponentAssembly.MapComponentFromParent, and the world-frame rotation
//   is mapped into that subassembly's local frame (R_local = Mbᵀ·R·Mb, with a
//   matching translation) so it rotates about the correct world axis/pivot.
// * Top-level components (cover, body) are unaffected and move exactly as before.
//
// v3.7 changes  (outlet now rotates everything connected to it)
// ───────────────────────────────────────────────────────────────────────────
// * Problem: the outlet rotated alone. Anything mounted on it (flanges, gaskets,
//   pipe stubs, fittings) stayed behind — unlike the cover, which already drags
//   its vac valve along.
// * Fix: before the constraints are suppressed, we walk the assembly CONSTRAINT
//   GRAPH starting from the outlet and collect every component rigidly connected
//   to it (BFS over the constraints). All of them are then rotated with the same
//   angle / axis / pivot as the outlet — the outlet's equivalent of the cover +
//   vac-valve group.
// * The body, cover and vac valve are excluded from the traversal, so the outlet
//   rotation can never cross into them or drag the fixed body out of place.
// * The graph MUST be built before suppression — a suppressed constraint no
//   longer describes the connection we need to follow.
//
// v3.8 changes  (outlet connectivity now works for the NESTED outlet)
// ───────────────────────────────────────────────────────────────────────────
// * v3.7 only scanned TOP-LEVEL constraints, so for this assembly — where the
//   outlet lives inside the body subassembly (see v3.6) — it found nothing and
//   the outlet still rotated alone.
// * Fix: the connectivity walk now scans the constraints in the part that
//   actually positions the outlet among its siblings — its owning subassembly
//   (Component.Parent.Prototype) as well as the top assembly. Parts it finds
//   are mapped back to the displayed tree by prototype, then rotated through the
//   same proven (nested-safe) path that moves the outlet.
// * The walk stops at the body / cover / vac — by tag AND by name — so it can
//   never flood into the fixed body shell, even when that shell appears as a
//   different occurrence inside the subassembly.
// * Before the (slow) rotation commits, the auto-detected group is listed in a
//   confirmation box: Yes = outlet + parts, No = outlet only, Cancel = abort.
//   Every part found (and every stop) is written to the Change Log, so a single
//   run shows exactly what was detected.
//
// v3.9 changes  (compile fixes — wrong API names + missing assembly reference)
// ───────────────────────────────────────────────────────────────────────────
// * HashSet<T> lives in System.Core.dll, which NX's journal Play compiler does
//   not reference (only mscorlib). Replaced it with a small Dictionary-backed
//   TagSet so the connectivity sets compile. (Func/Action compiled because they
//   are in mscorlib in .NET 4.0; HashSet stayed in System.Core.dll.)
// * Corrected the constraint API to the real NXOpen.Positioning members
//   (the names used before did not exist in NX):
//       Constraint.GetReferences()             not GetConstraintReferences()
//       ConstraintReference.GetGeometry()       not the .Geometry property
//       ConstraintReference.GetMovableObject()  (new — the component being
//                                                positioned, tried first)
//   The component for each reference is the movable object when it is a
//   Component, else the geometry's NXObject.OwningComponent.
//
// v3.10 changes  (fix outlet "memory access violation" — back again, in the scan)
// ───────────────────────────────────────────────────────────────────────────
// * Symptom: clicking Apply with an outlet angle crashed NX instantly with
//   "Internal error: memory access violation" — BEFORE the connectivity
//   confirmation pop-up, so the crash was in the new scan, not in any move.
// * Root cause: the scan dereferenced constraint GEOMETRY — GetGeometry() and
//   then OwningComponent / IsOccurrence on a face/edge occurrence. In a
//   partially-loaded assembly that is a native access violation, and a native
//   AV is a Corrupted-State Exception that managed try/catch CANNOT trap, so it
//   crashes NX outright. (SuppressAllConstraints never crashed because it only
//   touches Constraint.Suppressed, never geometry.)
// * Fix: CompOfRef now uses ONLY ConstraintReference.GetMovableObject() and a
//   pure managed "as Component" test. It never calls GetGeometry() and never
//   touches OwningComponent/IsOccurrence, so it cannot AV. The movable object of
//   a component constraint is the component, which is the connectivity we need;
//   a reference whose movable object is not a component just adds no edge.
// * Added a crash-surviving FILE log (temp\nx_rotator_crash.log), flushed per
//   line, plus per-constraint progress during the scan. If NX ever hard-crashes
//   again, the file's LAST line is the exact constraint/step that did it. The
//   in-dialog log now also force-repaints each line so progress is visible live.
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

    // Crash-surviving log. The in-dialog log lives in a TextBox that is lost if
    // NX hard-crashes (e.g. a native access violation), and it does not even
    // repaint mid-operation. This mirrors every log line to a file, flushed per
    // line, so after a crash the file's LAST line is exactly where it died.
    static string _crashLogPath;

    // Body reference lock: the body must ALWAYS stay in its reference position.
    // We remember its home placement and restore it after every operation so a
    // constraint can never drag it out of place when another part is rotated.
    static Component _bodyComp;
    static Point3d   _bodyHomeOrigin;
    static Matrix3x3 _bodyHomeMatrix;
    static bool      _bodyHomeSet;

    // A tiny set of Tags, backed by a Dictionary.
    //
    // Why not HashSet<T>? HashSet<T> lives in System.Core.dll, which NX's
    // journal Play compiler does NOT reference — only mscorlib is referenced.
    // That is why List/Dictionary/Queue compile, why Action (always mscorlib)
    // and Func<> (moved to mscorlib in .NET 4.0) compile, but HashSet<T> — which
    // stayed in System.Core.dll — does not:
    // "The type or namespace name 'HashSet' could not be found". This wrapper
    // gives the HashSet behaviour we use (Add returning false on a duplicate,
    // and Contains) without that missing reference. Do NOT replace it with
    // HashSet — that reintroduces the build break.
    class TagSet
    {
        Dictionary<Tag, bool> _d = new Dictionary<Tag, bool>();
        // Returns true if newly added, false if the tag was already present
        // (same semantics as HashSet<T>.Add).
        public bool Add(Tag t)
        {
            if (_d.ContainsKey(t)) return false;
            _d[t] = true;
            return true;
        }
        public bool Contains(Tag t) { return _d.ContainsKey(t); }
    }

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
            // Outlet must not match the body subassembly (e.g. "Body Assembly -
            // Straight Outlet" contains both BODY and OUTLET).
            if (autoOutlet < 0 && up.Contains("OUTLET") && !up.Contains("BODY") && i != autoBody) autoOutlet = i;
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
        AppendLog("Suppressed " + suppressed.Count + " constraint(s).");
        return suppressed;
    }

    static void RestoreConstraints(List<NXOpen.Positioning.Constraint> constraints)
    {
        foreach (NXOpen.Positioning.Constraint c in constraints)
        {
            try { c.Suppressed = false; } catch { }
        }
    }


    // ─── Outlet connectivity (rotate everything attached to the outlet) ─────────
    // Mirrors the cover behaviour (cover + vac valve), but generalised: starting
    // from the outlet we walk the assembly constraint graph and collect every
    // component rigidly connected to it, then rotate them all with the same
    // angle / axis / pivot. The body, cover and vac valve are excluded so the
    // traversal can never cross into them or drag them along.
    //
    // IMPORTANT: build this graph BEFORE the constraints are suppressed — a
    // suppressed constraint no longer describes the connection we must follow.

    // Resolve the assembly component a constraint reference is on.
    //
    // CRASH-SAFETY (this is what caused "Internal error: memory access violation"
    // the instant the outlet scan started): we use ONLY the reference's movable
    // object, and only when it is itself a Component. We deliberately do NOT call
    // GetGeometry() and do NOT touch OwningComponent / IsOccurrence on the
    // reference geometry. Dereferencing constraint geometry (a face/edge
    // occurrence) in a partially-loaded assembly triggers a NATIVE access
    // violation, and a native AV is a Corrupted-State Exception that a managed
    // try/catch cannot trap — so it takes NX down instead of being swallowed.
    //
    // The movable object of a component constraint IS the component being
    // positioned, which is exactly the component-to-component connectivity we
    // need — and "obj as Component" is a pure managed type test that never calls
    // into native NX, so it cannot AV. If a reference's movable object is not a
    // Component (e.g. a grounded/auto reference), it simply contributes no edge.
    //   * Constraint.GetReferences()             -> ConstraintReference[]
    //   * ConstraintReference.GetMovableObject()  -> NXObject (the component)
    static Component CompOfRef(NXOpen.Positioning.ConstraintReference cr)
    {
        if (cr == null) return null;
        NXObject mov = null;
        try { mov = cr.GetMovableObject(); }
        catch { return null; }
        return mov as Component;   // managed cast only — no native geometry deref
    }

    static void AddEdge(Dictionary<Tag, List<Tag>> graph, Tag a, Tag b)
    {
        if (a == b) return;
        List<Tag> la;
        if (!graph.TryGetValue(a, out la)) { la = new List<Tag>(); graph[a] = la; }
        if (!la.Contains(b)) la.Add(b);
        List<Tag> lb;
        if (!graph.TryGetValue(b, out lb)) { lb = new List<Tag>(); graph[b] = lb; }
        if (!lb.Contains(a)) lb.Add(a);
    }

    // Build an undirected component graph from every constraint in the part: two
    // components share an edge if a constraint positions both (their movable
    // objects are connected). See CompOfRef for why we never touch geometry here.
    static Dictionary<Tag, List<Tag>> BuildConstraintGraph(
        Part scanPart, Dictionary<Tag, Component> compByTag)
    {
        var graph = new Dictionary<Tag, List<Tag>>();
        int nConstraints = 0;
        try
        {
            UFSession ufs = UFSession.GetUFSession();
            Tag tag = Tag.Null;
            ufs.Obj.CycleObjsInPart(scanPart.Tag, -1, ref tag);
            while (tag != Tag.Null)
            {
                NXOpen.Positioning.Constraint c =
                    NXOpen.Utilities.NXObjectManager.Get(tag) as NXOpen.Positioning.Constraint;
                if (c != null)
                {
                    nConstraints++;
                    // File-only progress: if a native AV still kills NX here, the
                    // crash log's LAST line is the constraint that did it.
                    CrashLog("    scan '" + SafePartName(scanPart) + "' constraint #" + nConstraints);
                    var comps = new List<Component>();
                    try
                    {
                        foreach (NXOpen.Positioning.ConstraintReference cr in c.GetReferences())
                        {
                            Component oc = CompOfRef(cr);
                            if (oc != null) { comps.Add(oc); compByTag[oc.Tag] = oc; }
                        }
                    }
                    catch { }
                    for (int i = 0; i < comps.Count; i++)
                        for (int j = i + 1; j < comps.Count; j++)
                            AddEdge(graph, comps[i].Tag, comps[j].Tag);
                }
                ufs.Obj.CycleObjsInPart(scanPart.Tag, -1, ref tag);
            }
        }
        catch (Exception ex)
        {
            AppendLog("Connectivity scan failed: " + ex.Message);
        }
        AppendLog("Scanned " + nConstraints + " constraint(s); found " +
                  graph.Count + " connected component node(s).");
        return graph;
    }

    // The part whose constraints position a component among its siblings: for a
    // top-level component this is the work part; for a nested component it is the
    // prototype part of its parent subassembly occurrence (where those sibling
    // constraints are actually authored).
    static Part OwningAssemblyPart(Part workPart, Component comp)
    {
        try
        {
            Component parent = comp.Parent;
            if (parent != null)
            {
                Part pp = parent.Prototype as Part;
                if (pp != null) return pp;
            }
        }
        catch { }
        return workPart;
    }

    static Tag TryProtoTag(Component c)
    {
        try { Part p = c.Prototype as Part; if (p != null) return p.Tag; }
        catch { }
        return Tag.Null;
    }

    static bool NameExcluded(Component c, List<string> keys)
    {
        if (keys == null || keys.Count == 0) return false;
        string up = GetBestName(c).ToUpper();
        foreach (string k in keys)
            if (!string.IsNullOrEmpty(k) && up.Contains(k)) return true;
        return false;
    }

    // Map a component found while scanning a subassembly back to the matching
    // occurrence in the displayed assembly tree (by prototype part), so it can be
    // rotated through the same proven path that moves the outlet itself.
    static Component MapToDisplayed(List<CompInfo> allComps, Component c)
    {
        Tag pt = TryProtoTag(c);
        if (pt == Tag.Null) return c;
        foreach (CompInfo ci in allComps)
            if (TryProtoTag(ci.Comp) == pt) return ci.Comp;
        return c;
    }

    static string SafePartName(Part p)
    {
        try { if (p != null) return p.Leaf; } catch { }
        return "(unknown part)";
    }

    // Every component constraint-connected to the seed (BFS over the constraint
    // graph of scanPart). The seed is matched into scanPart's own context by
    // prototype when its displayed-tree tag is not a node in that part's graph
    // (i.e. the outlet seen from the top assembly vs. inside its subassembly).
    // The walk stops at — and never crosses — anything excluded by tag or name
    // (body / cover / vac), so it cannot flood into the fixed body.
    static List<Component> CollectConnectedInPart(
        Part scanPart, Component seed, TagSet excludeTags, List<string> excludeNameKeys)
    {
        var compByTag = new Dictionary<Tag, Component>();
        var graph = BuildConstraintGraph(scanPart, compByTag);

        // Resolve the seed node in this part's own context.
        Tag seedTag = seed.Tag;
        if (!graph.ContainsKey(seedTag))
        {
            Tag protoTag = TryProtoTag(seed);
            if (protoTag != Tag.Null)
                foreach (var kv in compByTag)
                    if (TryProtoTag(kv.Value) == protoTag) { seedTag = kv.Key; break; }
        }

        var result  = new List<Component>();
        var visited = new TagSet();
        var queue   = new Queue<Tag>();
        visited.Add(seedTag);
        queue.Enqueue(seedTag);

        while (queue.Count > 0)
        {
            Tag t = queue.Dequeue();
            List<Tag> neighbours;
            if (!graph.TryGetValue(t, out neighbours)) continue;
            foreach (Tag nt in neighbours)
            {
                if (visited.Contains(nt)) continue;
                visited.Add(nt);                 // seen — do not revisit
                Component nc;
                if (!compByTag.TryGetValue(nt, out nc) || nc == null) continue;
                if (excludeTags.Contains(nt))
                {
                    AppendLog("      (stop at " + GetBestName(nc) + " — excluded)");
                    continue;                    // do not traverse through it
                }
                if (NameExcluded(nc, excludeNameKeys))
                {
                    AppendLog("      (stop at " + GetBestName(nc) + " — excluded by name)");
                    continue;
                }
                queue.Enqueue(nt);
                result.Add(nc);
            }
        }
        return result;
    }


    // ─── Core rotation ────────────────────────────────────────────────────────

    // Low-level move: rotate/translate one component, then immediately run an
    // NX update bracketed by an undo mark. Updating after EVERY move is what
    // keeps repeated MoveComponent calls from corrupting NX's internal state
    // (the "Internal error: memory access violation" crash). Returns false and
    // reports once if the move fails.
    // Lowest level: do the actual MoveComponent on a specific ComponentAssembly,
    // bracketed by an undo mark + NX update so repeated moves stay stable.
    static bool MoveRaw(ComponentAssembly asm, Component comp, Vector3d delta,
        double[,] R, string label, bool loud)
    {
        try
        {
            NXOpen.Session.UndoMarkId mk =
                theSession.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible, label);
            asm.MoveComponent(comp, delta, ToNXMatrix(R));
            theSession.UpdateManager.DoUpdate(mk);
            return true;
        }
        catch (Exception ex)
        {
            if (loud)
            {
                string nm = GetBestName(comp);
                AppendLog("MoveComponent FAILED on '" + nm + "': " + ex.Message);
                MessageBox.Show(
                    "Could not rotate:\n   " + nm + "\n\n" + ex.Message + "\n\n" +
                    "The component's part may be read-only or not fully loaded. Fully load " +
                    "the assembly (or open the owning subassembly) and try again.",
                    "Rotation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                AppendLog("Body lock could not correct drift (" + ex.Message + ").");
            }
            return false;
        }
    }

    // Move a component that may be nested. ComponentAssembly.MoveComponent only
    // works on components it owns directly, so for a nested component we move it
    // inside its own owning subassembly (Component.DirectOwner) instead. Both the
    // component reference (MapComponentFromParent) and the world-frame transform
    // are mapped into that subassembly's local coordinate frame.
    static bool MoveInContext(Part topPart, Component comp,
        Vector3d worldDelta, double[,] Rw, string label, bool loud)
    {
        ComponentAssembly owner = null;
        try { owner = comp.DirectOwner; } catch { }
        if (owner == null) owner = topPart.ComponentAssembly;

        bool topLevel = (owner.Tag == topPart.ComponentAssembly.Tag);
        if (topLevel)
            return MoveRaw(owner, comp, worldDelta, Rw, label, loud);

        // Nested: map the transform from world into the owning subassembly frame.
        Vector3d delta = worldDelta;
        double[,] R = Rw;
        Component target = comp;
        try
        {
            Component parent = comp.Parent;          // owning subassembly occurrence
            Point3d Ob; Matrix3x3 Mbn;
            parent.GetPosition(out Ob, out Mbn);     // its placement in the displayed part
            double[,] Mb  = MatOf(Mbn);
            double[,] MbT = Transpose(Mb);

            // R_local = Mbᵀ · Rw · Mb
            R = MatMul(MatMul(MbT, Rw), Mb);

            // delta_local = Mbᵀ · (Rw·Ob + worldDelta − Ob)
            double[] rwOb = MatVec(Rw, Ob.X, Ob.Y, Ob.Z);
            double[] dl = MatVec(MbT,
                rwOb[0] + worldDelta.X - Ob.X,
                rwOb[1] + worldDelta.Y - Ob.Y,
                rwOb[2] + worldDelta.Z - Ob.Z);
            delta = new Vector3d(dl[0], dl[1], dl[2]);
        }
        catch (Exception ex)
        {
            AppendLog("Context mapping failed (" + ex.Message + "); using world transform.");
        }

        // Map the component reference into the owning subassembly's context.
        try
        {
            Component mapped = owner.MapComponentFromParent(comp);
            if (mapped != null) target = mapped;
        }
        catch { }

        AppendLog("(nested component — moving inside its subassembly)");
        return MoveRaw(owner, target, delta, R, label, loud);
    }

    // Rotate one component by angleDeg about the given axis, around the pivot.
    static bool MoveAround(Part topPart, Component comp,
        double angleDeg, string axis, Point3d pivot, string label)
    {
        double[,] dR = BuildDR(angleDeg, axis);
        Vector3d delta = ComputeDelta(dR, pivot);
        return MoveInContext(topPart, comp, delta, dR, label, true);
    }

    // Validate that a rotation angle is a whole multiple of its step angle.
    // Returns false (and shows why) if the angle is not feasible.
    static bool StepOk(double angle, double step, string name)
    {
        if (angle == 0) return true;
        if (step <= 0)
        {
            MessageBox.Show(
                "Enter the " + name + " step angle first.\n\n" +
                "The " + name.ToLower() + " has indexed / stepped positions, so its rotation " +
                "must be a whole multiple of the step angle. This prevents moving it to a " +
                "position that is not physically feasible.",
                "Step angle required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        double ratio = angle / step;
        double nearest = Math.Round(ratio);
        if (Math.Abs(angle - nearest * step) > 0.001)
        {
            double low  = Math.Floor(ratio)  * step;
            double high = Math.Ceiling(ratio) * step;
            MessageBox.Show(
                name + " angle " + angle + "° is not a multiple of the step angle " +
                step + "°.\n\nNearest feasible values: " + low + "° or " + high + "°.",
                "Invalid " + name + " angle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    // Build the undo/redo record for a rotation that was applied.
    static RotationOp MakeOp(Component comp, double angleDeg, string axis, Point3d pivot)
    {
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

        // BODY  (fixed reference – held in place, never moves)
        FL(form, "BODY  (fixed reference – held in place, will NOT move)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,100,200), lx, y, 460); y += 20;
        ComboBox bodyCombo = FC(form, allComps, lx, y, cw,
            autoBody >= 0 ? autoBody : 0); y += 30;
        CaptureBodyHome(allComps[bodyCombo.SelectedIndex].Comp);
        bodyCombo.SelectedIndexChanged += delegate
        {
            CaptureBodyHome(allComps[bodyCombo.SelectedIndex].Comp);
            AppendLog("Body reference set to: " + GetBestName(allComps[bodyCombo.SelectedIndex].Comp));
        };

        // COVER  + step angle
        FL(form, "COVER  (will be rotated)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(180,0,0), lx, y, 300); y += 20;
        ComboBox coverCombo = FC(form, allComps, lx, y, cw,
            autoCover >= 0 ? autoCover : Math.Min(1, allComps.Count-1)); y += 26;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 100);
        TextBox coverAngleBox = FT(form, "0", lx+105, y, 70);
        FL(form, "Step angle (deg):", form.Font, Color.Black, lx+200, y+3, 110);
        TextBox coverStepBox = FT(form, "", lx+315, y, 70); y += 24;
        FL(form, "Cover angle must be a whole multiple of the step angle (its indexed positions).",
            form.Font, Color.Gray, lx, y, 590); y += 26;

        // Vac valve: ALWAYS rotates with the cover (mandatory – not a user
        // option). Auto-detected by name; shown read-only so you can see which
        // part it resolved to, but it cannot be turned off or reassigned here.
        Component vacComp = (autoVac >= 0) ? allComps[autoVac].Comp : null;
        string vacName = vacComp != null ? GetBestName(vacComp) : "(none auto-detected)";
        FL(form, "Vac valve (always rotates with cover): " + Trunc(vacName, 60),
            form.Font, Color.FromArgb(120,80,0), lx, y, 590); y += 24;

        // OUTLET  + step angle
        FL(form, "OUTLET  (rotated with everything connected to it)",
            new Font("Segoe UI", 9, FontStyle.Bold), Color.FromArgb(0,130,0), lx, y, 400); y += 20;
        ComboBox outletCombo = FC(form, allComps, lx, y, cw,
            autoOutlet >= 0 ? autoOutlet : Math.Min(2, allComps.Count-1)); y += 26;
        FL(form, "Rotation angle:", form.Font, Color.Black, lx, y+3, 100);
        TextBox outletAngleBox = FT(form, "0", lx+105, y, 70);
        FL(form, "Step angle (deg):", form.Font, Color.Black, lx+200, y+3, 110);
        TextBox outletStepBox = FT(form, "", lx+315, y, 70); y += 24;
        FL(form, "Multiple of its step angle. All parts constrained to the outlet rotate with it.",
            form.Font, Color.Gray, lx, y, 590); y += 26;

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

            double coverAngle, outletAngle, coverStep, outletStep;
            if (!double.TryParse(coverAngleBox.Text,   out coverAngle))  coverAngle  = 0;
            if (!double.TryParse(outletAngleBox.Text,  out outletAngle)) outletAngle = 0;
            if (!double.TryParse(coverStepBox.Text,    out coverStep))   coverStep   = 0;
            if (!double.TryParse(outletStepBox.Text,   out outletStep))  outletStep  = 0;

            bool restore = restoreChk.Checked;

            // Step-angle enforcement: cover and outlet each only have discrete
            // indexed positions, so each rotation must be a whole multiple of its
            // own step angle.
            if (!StepOk(coverAngle, coverStep, "Cover")) return;
            if (!StepOk(outletAngle, outletStep, "Outlet")) return;

            if (coverAngle == 0 && outletAngle == 0)
            {
                MessageBox.Show("Both angles are 0 — nothing to rotate.",
                    "Nothing applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Outlet group: collect everything rigidly connected to the outlet
            // BEFORE the constraints are suppressed (a suppressed constraint no
            // longer describes the connection). The scan runs in the part that
            // actually positions the outlet among its siblings — its owning
            // subassembly when the outlet is nested (this assembly) AND the top
            // assembly — and the body / cover / vac are excluded by tag AND by
            // name so the walk can never flood into the fixed body.
            List<Component> outletFollowers = new List<Component>();
            if (outletAngle != 0)
            {
                Component outletSeed = allComps[outletCombo.SelectedIndex].Comp;

                var excludeTags = new TagSet();
                excludeTags.Add(allComps[bodyCombo.SelectedIndex].Comp.Tag);
                excludeTags.Add(allComps[coverCombo.SelectedIndex].Comp.Tag);
                if (vacComp != null) excludeTags.Add(vacComp.Tag);

                var excludeNames = new List<string>();
                excludeNames.Add("BODY");
                excludeNames.Add("COVER");
                if (vacComp != null) { excludeNames.Add("VAC"); excludeNames.Add("VALVE"); }

                // Scan both the top assembly and the outlet's owning subassembly,
                // wherever the positioning constraints happen to be authored.
                var raw = new List<Component>();
                AppendLog("Outlet connectivity scan in '" + SafePartName(workPart) + "' (top level).");
                raw.AddRange(CollectConnectedInPart(workPart, outletSeed, excludeTags, excludeNames));
                Part subPart = OwningAssemblyPart(workPart, outletSeed);
                if (subPart.Tag != workPart.Tag)
                {
                    AppendLog("Outlet connectivity scan in '" + SafePartName(subPart) + "' (outlet subassembly).");
                    raw.AddRange(CollectConnectedInPart(subPart, outletSeed, excludeTags, excludeNames));
                }

                // Map each connected part back to its occurrence in the displayed
                // tree (so it rotates through the same proven path as the outlet)
                // and dedupe by prototype.
                var seenProto = new TagSet();
                foreach (Component rc in raw)
                {
                    Component disp = MapToDisplayed(allComps, rc);
                    if (disp == null || disp.Tag == outletSeed.Tag) continue;
                    Tag pkey = TryProtoTag(disp);
                    if (pkey != Tag.Null && !seenProto.Add(pkey)) continue;
                    outletFollowers.Add(disp);
                }

                AppendLog("Outlet connectivity: " + outletFollowers.Count +
                          " connected part(s) will rotate with the outlet.");
                foreach (Component f in outletFollowers)
                    AppendLog("      + " + GetBestName(f));
                if (outletFollowers.Count == 0)
                    AppendLog("      (nothing constraint-linked to the outlet was found; only the outlet moves.)");

                // Confirm the auto-detected group before the slow rotation commits.
                if (outletFollowers.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("These " + outletFollowers.Count +
                                  " part(s) are connected to the outlet and will rotate WITH it:");
                    sb.AppendLine();
                    foreach (Component f in outletFollowers) sb.AppendLine("    • " + GetBestName(f));
                    sb.AppendLine();
                    sb.AppendLine("Yes     = rotate the outlet AND these parts");
                    sb.AppendLine("No      = rotate the outlet ONLY");
                    sb.AppendLine("Cancel  = do not rotate the outlet at all");
                    DialogResult ans = MessageBox.Show(sb.ToString(), "Confirm outlet group",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (ans == DialogResult.Cancel)
                    {
                        outletAngle = 0; outletFollowers.Clear();
                        AppendLog("Outlet rotation cancelled by user.");
                    }
                    else if (ans == DialogResult.No)
                    {
                        outletFollowers.Clear();
                        AppendLog("Rotating outlet only (connected parts skipped by user).");
                    }
                }
            }

            // After an outlet-only cancel there may be nothing left to rotate.
            if (coverAngle == 0 && outletAngle == 0)
            {
                AppendLog("Nothing to rotate.");
                return;
            }

            // Suppress constraints ONCE for the whole action (not per move) so
            // the solver cannot revert anything and we avoid hammering the model.
            var suppressed = SuppressAllConstraints(workPart);

            var newOps  = new List<RotationOp>();
            var applied = new List<string>();

            if (coverAngle != 0)
            {
                CompInfo ci = allComps[coverCombo.SelectedIndex];
                AppendLog("Cover target: " + ci.FullName + "  (tree level " + ci.Level + ")");
                Component coverComp = ci.Comp;
                if (MoveAround(workPart, coverComp, coverAngle, axis, pivot, "Rotate cover"))
                {
                    RotationOp op = MakeOp(coverComp, coverAngle, axis, pivot);
                    newOps.Add(op); applied.Add(op.Description);
                    AppendLog("APPLY  " + op.Description);

                    // Vac valve ALWAYS follows the cover: same angle/axis/pivot.
                    if (vacComp != null && vacComp != coverComp)
                    {
                        if (MoveAround(workPart, vacComp, coverAngle, axis, pivot, "Rotate vac valve"))
                        {
                            RotationOp vop = MakeOp(vacComp, coverAngle, axis, pivot);
                            newOps.Add(vop); applied.Add(vop.Description + "  [with cover]");
                            AppendLog("APPLY  " + vop.Description + "  [with cover]");
                        }
                    }
                    else if (vacComp == null)
                    {
                        AppendLog("Note: vac valve not auto-detected; rotated cover only.");
                    }
                }
            }

            if (outletAngle != 0)
            {
                CompInfo oi = allComps[outletCombo.SelectedIndex];
                AppendLog("Outlet target: " + oi.FullName + "  (tree level " + oi.Level + ")");
                Component outletComp = oi.Comp;
                if (MoveAround(workPart, outletComp, outletAngle, axis, pivot, "Rotate outlet"))
                {
                    RotationOp op = MakeOp(outletComp, outletAngle, axis, pivot);
                    newOps.Add(op); applied.Add(op.Description);
                    AppendLog("APPLY  " + op.Description);

                    // Everything connected to the outlet follows it with the same
                    // angle / axis / pivot (the outlet's equivalent of cover + vac).
                    foreach (Component f in outletFollowers)
                    {
                        if (f == outletComp) continue;
                        AppendLog("Moving connected part: " + GetBestName(f));
                        if (MoveAround(workPart, f, outletAngle, axis, pivot, "Rotate outlet-attached part"))
                        {
                            RotationOp fop = MakeOp(f, outletAngle, axis, pivot);
                            newOps.Add(fop); applied.Add(fop.Description + "  [with outlet]");
                            AppendLog("APPLY  " + fop.Description + "  [with outlet]");
                        }
                    }
                }
            }

            RestoreBody(workPart);  // body stays in its reference position

            if (restore && suppressed.Count > 0)
            {
                RestoreConstraints(suppressed);
                AppendLog("Constraints RESTORED (position may revert on next update).");
            }

            if (newOps.Count == 0)
            {
                MessageBox.Show("Nothing was rotated (the move was rejected).",
                    "Nothing applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            history.AddRange(newOps);
            redo.Clear();           // a fresh action invalidates the redo branch
            refreshButtons();

            // Reset the angle fields to 0 so the same rotation is not applied
            // twice by mistake; a 0 here means "the change has been made".
            coverAngleBox.Text = "0";
            outletAngleBox.Text = "0";

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
            SuppressAllConstraints(workPart);
            if (!MoveAround(workPart, op.Comp, -op.AngleDeg, op.Axis, op.Pivot, "Undo rotate")) return;
            RestoreBody(workPart);
            history.RemoveAt(history.Count - 1);
            redo.Add(op);
            AppendLog("UNDO   " + op.Description);
            refreshButtons();
        };

        redoBtn.Click += delegate
        {
            if (redo.Count == 0) return;
            RotationOp op = redo[redo.Count - 1];
            SuppressAllConstraints(workPart);
            if (!MoveAround(workPart, op.Comp, op.AngleDeg, op.Axis, op.Pivot, "Redo rotate")) return;
            RestoreBody(workPart);
            redo.RemoveAt(redo.Count - 1);
            history.Add(op);
            AppendLog("REDO   " + op.Description);
            refreshButtons();
        };

        refreshButtons();
        InitCrashLog();
        AppendLog("Ready. Set angles and click Apply Rotation.");
        if (_crashLogPath != null)
            AppendLog("Crash log (read this if NX crashes): " + _crashLogPath);

        form.ShowDialog();
        AppendLog("Session ended: " + history.Count + " net operation(s) applied.");
        _logBox = null;
        _bodyComp = null; _bodyHomeSet = false;
        form.Dispose();
    }

    // Append one timestamped line to the single change-log panel.
    static void AppendLog(string line)
    {
        CrashLog(line);   // persist first, so a line survives even a hard crash
        if (_logBox == null) return;
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText(stamp + "  " + line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
        // Force an immediate repaint of just this control so progress is visible
        // before any long/blocking operation (and before a possible crash).
        // Update() repaints without pumping input messages, so it cannot cause
        // re-entrant button clicks the way Application.DoEvents() would.
        try { _logBox.Update(); } catch { }
    }

    // Start a fresh crash log for this session (in the temp folder). Best-effort:
    // if the file cannot be created, crash logging is simply disabled.
    static void InitCrashLog()
    {
        try
        {
            _crashLogPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nx_rotator_crash.log");
            System.IO.File.WriteAllText(_crashLogPath,
                "NX Assembly Component Rotator — session " + DateTime.Now + Environment.NewLine);
        }
        catch { _crashLogPath = null; }
    }

    // Append one line to the crash log, opening+closing the file each time so the
    // line is flushed to disk immediately (survives a hard NX crash).
    static void CrashLog(string line)
    {
        if (_crashLogPath == null) return;
        try
        {
            System.IO.File.AppendAllText(_crashLogPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
        }
        catch { }
    }


    // ─── Body reference lock ──────────────────────────────────────────────────
    // The body is the fixed reference. We record its home placement once and
    // restore it after every move so it can never be dragged out of position.

    static void CaptureBodyHome(Component comp)
    {
        _bodyComp = comp;
        _bodyHomeSet = false;
        if (comp == null) return;
        try
        {
            Point3d o; Matrix3x3 m;
            comp.GetPosition(out o, out m);
            _bodyHomeOrigin = o; _bodyHomeMatrix = m; _bodyHomeSet = true;
        }
        catch { }
    }

    // Force the body back to its captured reference placement. The incremental
    // transform that maps the body's current placement (curO, curM) back to its
    // home (homeO, homeM) is:  R = homeM * curM^T ,  t = homeO - R * curO.
    static void RestoreBody(Part workPart)
    {
        if (!_bodyHomeSet || _bodyComp == null) return;
        try
        {
            Point3d curO; Matrix3x3 curM;
            _bodyComp.GetPosition(out curO, out curM);

            double[,] R  = MatMul(MatOf(_bodyHomeMatrix), Transpose(MatOf(curM)));
            double[]  rc = MatVec(R, curO.X, curO.Y, curO.Z);
            Vector3d  t  = new Vector3d(_bodyHomeOrigin.X - rc[0],
                                        _bodyHomeOrigin.Y - rc[1],
                                        _bodyHomeOrigin.Z - rc[2]);

            // Skip if the body is already at home (no measurable drift). This is
            // the normal case, so the body lock costs nothing when constraints
            // are properly suppressed.
            bool transZero = Math.Abs(t.X) < 1e-6 && Math.Abs(t.Y) < 1e-6 && Math.Abs(t.Z) < 1e-6;
            if (IsIdentity(R) && transZero) return;

            AppendLog("Body drifted from reference; correcting (suppression may not be holding it).");
            if (MoveInContext(workPart, _bodyComp, t, R, "Body lock", false))
                AppendLog("Body held in reference position (corrected drift).");
        }
        catch (Exception ex)
        {
            AppendLog("Body lock failed: " + ex.Message);
        }
    }

    static double[,] MatOf(Matrix3x3 m)
    {
        return new double[3,3] {
            { m.Xx, m.Xy, m.Xz },
            { m.Yx, m.Yy, m.Yz },
            { m.Zx, m.Zy, m.Zz } };
    }

    static double[,] Transpose(double[,] a)
    {
        double[,] r = new double[3,3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i,j] = a[j,i];
        return r;
    }

    static double[,] MatMul(double[,] a, double[,] b)
    {
        double[,] r = new double[3,3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i,k] * b[k,j];
                r[i,j] = s;
            }
        return r;
    }

    static double[] MatVec(double[,] a, double x, double y, double z)
    {
        return new double[] {
            a[0,0]*x + a[0,1]*y + a[0,2]*z,
            a[1,0]*x + a[1,1]*y + a[1,2]*z,
            a[2,0]*x + a[2,1]*y + a[2,2]*z };
    }

    static bool IsIdentity(double[,] a)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double expected = (i == j) ? 1.0 : 0.0;
                if (Math.Abs(a[i,j] - expected) > 1e-9) return false;
            }
        return true;
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
