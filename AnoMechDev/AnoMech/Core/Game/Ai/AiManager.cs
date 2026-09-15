using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game.Ai;

// Drives slot-ordered party movement from a scenario's position functions.
// Owns jitter, run speed, and event scheduling. Position functions return an
// AiMove whose entries are scenario-local XZ coords — same space MoveTo
// consumes, so AiManager forwards them as-is. Eye-spawn flip and slot
// reordering are handled inside the AiMove before it reaches here.
public sealed class AiManager
{
    private const float RunSpeed = 6f;
    private const float DefaultJitter = 0.3f;

    private readonly SimWorld world;
    private readonly bool deferToArrival;
    private readonly Random rng = new();

    public AiManager(SimWorld world, bool deferToArrival = false)
    {
        this.world = world;
        this.deferToArrival = deferToArrival;
    }

    // Schedule a slot-move at `time`. `positions` is evaluated at fire-time;
    // null entries in the returned AiMove are skipped (no movement that slot).
    // Timed moves normally leave immediately at a paced speed. Imported timelines
    // opt into deferToArrival: wait until normal-speed travel reaches arrivalTime.
    // If the destination is too far away, leave immediately and arrive late.
    public void Move(float time, Func<IAiMove> positions, float jitter = DefaultJitter, float arrivalTime = 0f,
        Func<IReadOnlyList<Vector2?>>? practiceReference = null)
    {
        var practiceStep = world.PracticePositions.Register(
            world.Events.Time + MathF.Max(0f, time),
            arrivalTime > time ? world.Events.Time + arrivalTime : 0f);
        world.Events.Add(time, () =>
        {
            var move = positions();
            Vector2?[]? resolved = null;
            if (world.PracticePositions.IsActive)
            {
                var original = new Vector2?[PracticePositionLogic.PartySlotCount];
                for (var i = 0; i < original.Length; i++)
                    original[i] = move[i];
                resolved = world.PracticePositions.Resolve(practiceStep, original, practiceReference?.Invoke());
            }

            for (int i = 0; i < PracticePositionLogic.PartySlotCount; i++)
            {
                var local = resolved is null ? move[i] : resolved[i];
                if (local is not { } targetLocal) continue;
                var member = world.Party.Get(i);
                if (member == null || !member.IsAlive()) continue;
                var target = Jitter(new Vector3(targetLocal.X, 0f, targetLocal.Y), jitter);

                if (arrivalTime > time)
                {
                    var dx = target.X - member.Position.X;
                    var dz = target.Z - member.Position.Z;
                    var distance = MathF.Sqrt(dx * dx + dz * dz);
                    if (deferToArrival)
                    {
                        var delay = arrivalTime - time - distance / RunSpeed;
                        if (delay > 0f)
                            world.Events.Add(delay, () => member.MoveTo(target));
                        else
                            member.MoveTo(target);
                        continue;
                    }
                    // Default mode keeps the existing immediate, paced departure.
                    var avail = arrivalTime - time;
                    var sp = Math.Clamp(distance / avail, 4f, RunSpeed);
                    member.MoveTo(target, sp);
                    continue;
                }
                member.MoveTo(target);
            }
        });
    }

    // Schedule temporary death-immunity for `role` at scenario-time `time`, lasting
    // `seconds` (default 10). Wraps SimParty.GiveInvuln so AI strats can read top-to-
    // bottom alongside Move/Automarker, e.g. ai.GiveInvuln(28f, PartyRole.OffTank).
    public void GiveInvuln(float time, PartyRole role, float seconds = 10f)
        => world.Events.Add(time, () => world.Party.GiveInvuln(role, seconds));

    public void Automarker(float time, Func<Dictionary<PartyRole, Sign>> mapping)
    {
        world.Events.Add(time, () =>
        {
            Markings.ClearAll();
            foreach (var (role, sign) in mapping())
                if (world.Party.Get(role) is { } member && member.IsAlive())
                    Markings.Set(sign, member.GameObjectId);
        });
    }

    private Vector3 Jitter(Vector3 target, float radius)
    {
        var theta = rng.NextDouble() * 2.0 * Math.PI;
        var r = radius * MathF.Sqrt((float)rng.NextDouble());
        return new Vector3(
            target.X + r * MathF.Cos((float)theta),
            target.Y,
            target.Z + r * MathF.Sin((float)theta));
    }
}
