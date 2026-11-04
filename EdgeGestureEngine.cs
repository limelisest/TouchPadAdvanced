namespace TouchPadAdvanced;

public enum GestureInteraction { Invoke, Begin, Update, Commit, Cancel }
public sealed record GestureInvocation(GestureBinding Binding, int Steps = 1,
    GestureInteraction Interaction = GestureInteraction.Invoke, int SelectionOffset = 0);
public enum GesturePhase { WaitingForRelease, Idle, Tracking, Triggered, Cancelled, Pointer }

// Pure frame-to-action recognition. No OS calls, UI timers or wall-clock assumptions.
public sealed class EdgeGestureEngine
{
    private const double DirectionRatio = 1.5;
    private const double CorridorTolerance = 0.03;
    private readonly GestureSettings settings;
    private readonly List<GestureSpec> candidates = [];
    private bool blocked, consumed;
    private int? contactId;
    private double startX, startY, anchor;
    private PadEdge? lockedEdge;
    private GestureSpec? selection;
    private int selectionOffset;
    internal bool OwnsPointer { get; private set; }
    internal GesturePhase Phase { get; private set; }
    internal (double X, double Y)? ActiveOrigin => contactId is not null && OwnsPointer ? (startX, startY) : null;

    internal EdgeGestureEngine(GestureSettings settings, bool waitForRelease = true)
    {
        settings.Validate();
        this.settings = settings;
        blocked = waitForRelease;
        Phase = blocked ? GesturePhase.WaitingForRelease : GesturePhase.Idle;
    }

    internal GestureInvocation? Cancel()
    {
        var cancellation = EndSelection(GestureInteraction.Cancel);
        OwnsPointer = false;
        blocked = true;
        contactId = null;
        candidates.Clear();
        lockedEdge = null;
        consumed = false;
        Phase = GesturePhase.Cancelled;
        return cancellation;
    }

    internal GestureInvocation? Process(TouchFrame frame)
    {
        var active = frame.Contacts.Where(c => c.Tip).ToArray();
        if (active.Length == 0 && frame.Buttons == 0)
        {
            var completion = EndSelection(GestureInteraction.Commit);
            OwnsPointer = false;
            blocked = consumed = false;
            contactId = null;
            candidates.Clear();
            lockedEdge = null;
            Phase = GesturePhase.Idle;
            return completion;
        }
        if (blocked) return null;
        if (active.Length != 1 || frame.Buttons != 0 || active[0].Confidence == false)
        {
            return Cancel();
        }
        var contact = active[0];
        double x = contact.NormalizedX, y = contact.NormalizedY;
        if (contact.XRange.Max <= contact.XRange.Min || contact.YRange.Max <= contact.YRange.Min ||
            !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
        {
            return Cancel();
        }

        if (contactId is null)
        {
            contactId = contact.Id;
            TrackFrom(x, y);
            return null;
        }
        if (contactId != contact.Id)
        {
            return Cancel();
        }
        if (selection is not null) return UpdateSelection(x, y);
        if (consumed) return null;
        if (candidates.Count == 0)
        {
            TrackFrom(x, y); // A pointer contact may enter a bound region without lifting.
            return null;    // Entry displacement belongs to pointer motion, not the gesture.
        }

        if (lockedEdge is { } edge)
        {
            if (!InCorridor(edge, x, y))
            {
                Cancel();
                return null;
            }
            double position = Along(edge, x, y);
            double delta = position - anchor;
            if (delta == 0) return null;
            var spec = GestureCatalog.All.Single(s => s.Edge == edge && !s.Inward && s.Positive == (delta > 0));
            var binding = settings.Binding(spec.Id);
            double threshold = GestureCatalog.Repeats(binding.Action)
                ? settings.RepeatDistance : settings.TriggerDistance;
            if (Math.Abs(delta) + 1e-9 < threshold) return null;
            if (binding.Action == GestureAction.None)
            {
                anchor = position;
                return null;
            }
            int steps = GestureCatalog.Repeats(binding.Action)
                ? (int)Math.Floor((Math.Abs(delta) + 1e-9) / threshold) : 1;
            anchor += Math.Sign(delta) * threshold * steps;
            consumed = !GestureCatalog.Repeats(binding.Action);
            Phase = GesturePhase.Triggered;
            return new GestureInvocation(binding, steps);
        }

        double dx = x - startX, dy = y - startY;
        // Corners own diagonal inward motion; adjacent edges own tangential motion.
        foreach (var spec in candidates.Where(s => s.Corner is not null))
        {
            var (sx, sy) = InwardSigns(spec.Corner!.Value);
            double inwardX = sx * dx, inwardY = sy * dy;
            if (inwardX <= 0 || inwardY <= 0) continue;
            if (inwardX > inwardY * DirectionRatio || inwardY > inwardX * DirectionRatio) continue;
            if ((inwardX + inwardY) / Math.Sqrt(2) + 1e-9 < settings.TriggerDistance) continue;
            if (GestureCatalog.Selects(settings.Binding(spec.Id).Action)) return BeginSelection(spec, x, y);
            consumed = true;
            Phase = GesturePhase.Triggered;
            return new GestureInvocation(settings.Binding(spec.Id));
        }

        // A perpendicular swipe must remain armed after leaving its starting band.
        foreach (var spec in candidates.Where(s => s.Inward))
        {
            double normal = spec.Edge switch
            {
                PadEdge.Top => dy, PadEdge.Bottom => -dy,
                PadEdge.Left => dx, PadEdge.Right => -dx,
                _ => throw new InvalidOperationException()
            };
            double tangent = Along(spec.Edge!.Value, dx, dy);
            if (normal + 1e-9 < settings.TriggerDistance ||
                normal <= Math.Abs(tangent) * DirectionRatio) continue;
            if (GestureCatalog.Selects(settings.Binding(spec.Id).Action)) return BeginSelection(spec, x, y);
            consumed = true;
            Phase = GesturePhase.Triggered;
            return new GestureInvocation(settings.Binding(spec.Id));
        }

        candidates.RemoveAll(s => !s.Inward && s.Edge is { } side && !InCorridor(side, x, y));
        foreach (var spec in candidates.Where(s => s.Edge is not null && !s.Inward))
        {
            var side = spec.Edge!.Value;
            double delta = Along(side, dx, dy);
            double perpendicular = side is PadEdge.Top or PadEdge.Bottom ? dy : dx;
            if (delta == 0 || (delta > 0) != spec.Positive ||
                Math.Abs(delta) + 1e-9 < settings.TriggerDistance ||
                Math.Abs(delta) <= Math.Abs(perpendicular) * DirectionRatio) continue;

            var binding = settings.Binding(spec.Id);
            if (GestureCatalog.Selects(binding.Action)) return BeginSelection(spec, x, y);
            bool repeats = GestureCatalog.Repeats(binding.Action);
            int steps = repeats
                ? 1 + (int)Math.Floor((Math.Abs(delta) - settings.TriggerDistance + 1e-9) / settings.RepeatDistance)
                : 1;
            anchor = Along(side, startX, startY) + Math.Sign(delta) *
                (settings.TriggerDistance + (steps - 1) * settings.RepeatDistance);
            lockedEdge = side;
            consumed = !repeats;
            Phase = GesturePhase.Triggered;
            return new GestureInvocation(binding, steps);
        }
        if (candidates.Count == 0) TrackFrom(x, y);
        return null;
    }

    private GestureInvocation BeginSelection(GestureSpec spec, double x, double y)
    {
        selection = spec;
        selectionOffset = QuantizeSelection(ProjectSelection(spec, x, y)) *
            GestureCatalog.SelectionDirection(settings.Binding(spec.Id).Action);
        Phase = GesturePhase.Triggered;
        return new GestureInvocation(settings.Binding(spec.Id), Interaction: GestureInteraction.Begin,
            SelectionOffset: selectionOffset);
    }

    private GestureInvocation? UpdateSelection(double x, double y)
    {
        var spec = selection!;
        int direction = GestureCatalog.SelectionDirection(settings.Binding(spec.Id).Action);
        double position = ProjectSelection(spec, x, y);
        int slot = QuantizeSelection(position);
        int previous = selectionOffset * direction;
        // A small return margin prevents selection flicker near each slot boundary.
        if (previous != 0 && (slot == 0 || Math.Sign(slot) == Math.Sign(previous) && Math.Abs(slot) < Math.Abs(previous)))
        {
            double boundary = settings.TriggerDistance + (Math.Abs(previous) - 1) * settings.SelectionDistance;
            if (Math.Abs(position) > boundary - settings.SelectionDistance * .15) return null;
        }
        int target = slot * direction;
        if (target == selectionOffset) return null;
        selectionOffset = target;
        return new GestureInvocation(settings.Binding(spec.Id), Interaction: GestureInteraction.Update,
            SelectionOffset: target);
    }

    private GestureInvocation? EndSelection(GestureInteraction interaction)
    {
        if (selection is null) return null;
        var result = new GestureInvocation(settings.Binding(selection.Id), Interaction: interaction,
            SelectionOffset: selectionOffset);
        selection = null;
        selectionOffset = 0;
        return result;
    }

    private int QuantizeSelection(double position)
    {
        double distance = Math.Abs(position);
        if (distance + 1e-9 < settings.TriggerDistance) return 0;
        return Math.Sign(position) * (1 + (int)Math.Floor(
            (distance - settings.TriggerDistance + 1e-9) / settings.SelectionDistance));
    }

    private double ProjectSelection(GestureSpec spec, double x, double y)
    {
        double dx = x - startX, dy = y - startY;
        if (spec.Corner is { } corner)
        {
            var (sx, sy) = InwardSigns(corner);
            return (sx * dx + sy * dy) / Math.Sqrt(2);
        }
        if (spec.Inward) return spec.Edge switch
        {
            PadEdge.Top => dy, PadEdge.Bottom => -dy, PadEdge.Left => dx, PadEdge.Right => -dx,
            _ => throw new InvalidOperationException()
        };
        return Along(spec.Edge!.Value, dx, dy) * (spec.Positive ? 1 : -1);
    }
    private void TrackFrom(double x, double y)
    {
        startX = x;
        startY = y;
        candidates.Clear();
        candidates.AddRange(GestureCatalog.All.Where(s =>
            settings.Binding(s.Id).Action != GestureAction.None && StartsInZone(s, x, y)));
        OwnsPointer = candidates.Count != 0;
        Phase = OwnsPointer ? GesturePhase.Tracking : GesturePhase.Pointer;
    }
    private bool StartsInZone(GestureSpec spec, double x, double y)
    {
        if (spec.Edge is { } edge)
        {
            if (Distance(edge, x, y) > settings.GetEdgeWidth(edge)) return false;
            if (!spec.Inward) return true;
            // Corner intersections retain diagonal and adjacent tangential gestures.
            return edge is PadEdge.Top or PadEdge.Bottom
                ? x > settings.GetEdgeWidth(PadEdge.Left) && x < 1 - settings.GetEdgeWidth(PadEdge.Right)
                : y > settings.GetEdgeWidth(PadEdge.Top) && y < 1 - settings.GetEdgeWidth(PadEdge.Bottom);
        }
        return spec.Corner switch
        {
            PadCorner.TopLeft => x <= settings.GetEdgeWidth(PadEdge.Left) && y <= settings.GetEdgeWidth(PadEdge.Top),
            PadCorner.TopRight => x >= 1 - settings.GetEdgeWidth(PadEdge.Right) && y <= settings.GetEdgeWidth(PadEdge.Top),
            PadCorner.BottomLeft => x <= settings.GetEdgeWidth(PadEdge.Left) && y >= 1 - settings.GetEdgeWidth(PadEdge.Bottom),
            PadCorner.BottomRight => x >= 1 - settings.GetEdgeWidth(PadEdge.Right) && y >= 1 - settings.GetEdgeWidth(PadEdge.Bottom),
            _ => false
        };
    }

    private bool InCorridor(PadEdge edge, double x, double y) =>
        Distance(edge, x, y) <= settings.GetEdgeWidth(edge) + CorridorTolerance;

    private static double Distance(PadEdge edge, double x, double y) => edge switch
    {
        PadEdge.Top => y, PadEdge.Bottom => 1 - y, PadEdge.Left => x, PadEdge.Right => 1 - x,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    private static double Along(PadEdge edge, double x, double y) =>
        edge is PadEdge.Top or PadEdge.Bottom ? x : y;

    private static (int X, int Y) InwardSigns(PadCorner corner) => corner switch
    {
        PadCorner.TopLeft => (1, 1), PadCorner.TopRight => (-1, 1),
        PadCorner.BottomLeft => (1, -1), PadCorner.BottomRight => (-1, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(corner))
    };
}
