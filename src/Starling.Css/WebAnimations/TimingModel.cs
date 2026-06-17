namespace Starling.Css.WebAnimations;

/// <summary>
/// Pure implementation of the Web Animations 1 timing model (§4).
/// Converts an <see cref="EffectTiming"/> + a local time into a
/// <see cref="ComputedTiming"/> that carries the phase, current iteration,
/// and eased iteration progress ready for an effect to consume.
/// <para>
/// This is a model-only slice: it does not implement the JavaScript WAAPI
/// (<c>Element.animate</c>, <c>Animation</c>, <c>KeyframeEffect</c>, etc.).
/// Those require JS-binding scaffolding beyond the scope of this layer.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>§4.5 — phases and states: https://www.w3.org/TR/web-animations-1/#animation-effect-phases-and-states</description></item>
///   <item><description>§4.6 — active duration: https://www.w3.org/TR/web-animations-1/#calculating-the-active-duration</description></item>
///   <item><description>§4.7 — simple iteration progress: https://www.w3.org/TR/web-animations-1/#calculating-the-simple-iteration-progress</description></item>
///   <item><description>§4.8 — directed + transformed progress: https://www.w3.org/TR/web-animations-1/#calculating-the-directed-progress</description></item>
/// </list>
/// </summary>
public static class TimingModel
{
    /// <summary>
    /// Compute the <see cref="ComputedTiming"/> for <paramref name="timing"/>
    /// at the given <paramref name="localTime"/> (in milliseconds).
    /// </summary>
    /// <param name="timing">Effect timing parameters.</param>
    /// <param name="localTime">
    /// Time in milliseconds relative to the effect's own timeline origin.
    /// </param>
    /// <returns>
    /// A <see cref="ComputedTiming"/> whose <c>Progress</c> is null when the
    /// effect has no output (idle or out-of-fill-range), and a value in [0, 1]
    /// otherwise.
    /// </returns>
    public static ComputedTiming ComputeProgress(EffectTiming timing, double localTime)
    {
        // §4.6 — active duration
        // active duration = iteration duration × iteration count.
        // Special case: if either factor is zero the active duration is zero.
        // https://www.w3.org/TR/web-animations-1/#calculating-the-active-duration
        var activeDuration = (timing.Duration == 0 || timing.Iterations == 0)
            ? 0.0
            : timing.Duration * timing.Iterations;

        // §4.5 — determine the phase.
        // The spec scales local time by playbackRate, but for the timing model
        // the playbackRate is handled at the Animation level (before localTime
        // is passed in).  We treat localTime as already scaled, matching the
        // simple algorithm in §4.5.
        // https://www.w3.org/TR/web-animations-1/#animation-effect-phases-and-states
        var beforeActiveBoundary = timing.Delay;
        var afterActiveBoundary = timing.Delay + activeDuration;

        AnimationPhase phase;
        if (localTime < beforeActiveBoundary)
        {
            phase = AnimationPhase.Before;
        }
        else if (!double.IsPositiveInfinity(activeDuration) && localTime >= afterActiveBoundary)
        {
            phase = AnimationPhase.After;
        }
        else
        {
            phase = AnimationPhase.Active;
        }

        // §4.5 — determine whether the fill mode yields output when out of the active interval.
        bool hasOutput = phase == AnimationPhase.Active
            || (phase == AnimationPhase.Before
                && (timing.Fill == FillMode.Backwards || timing.Fill == FillMode.Both))
            || (phase == AnimationPhase.After
                && (timing.Fill == FillMode.Forwards || timing.Fill == FillMode.Both));

        if (!hasOutput)
        {
            return new ComputedTiming(phase, null, null);
        }

        // §4.7 — simple iteration progress.
        // https://www.w3.org/TR/web-animations-1/#calculating-the-simple-iteration-progress
        //
        // active time = local time − delay  (clamped to the active interval for fill phases)
        double activeTime;
        if (phase == AnimationPhase.Before)
        {
            // Backwards fill: use the start of the active interval.
            activeTime = 0;
        }
        else if (phase == AnimationPhase.After)
        {
            // Forwards fill: use the end of the active interval.
            activeTime = activeDuration;
        }
        else
        {
            activeTime = localTime - timing.Delay;
        }

        // overall progress = (activeTime / duration) + iterationStart,
        // but guard against duration == 0.
        double overallProgress;
        if (timing.Duration == 0)
        {
            // §4.7: when duration is zero the effect is "at the end" during
            // the active phase.  Use the ceiling so iterationStart is honoured.
            overallProgress = Math.Floor(timing.IterationStart + timing.Iterations);
        }
        else
        {
            overallProgress = activeTime / timing.Duration + timing.IterationStart;
        }

        // §4.7 — current iteration index.
        int currentIteration;
        double simpleIterationProgress;

        if (phase == AnimationPhase.After && double.IsPositiveInfinity(timing.Iterations))
        {
            // Infinite iterations can never finish; stay at iteration 0 progress 1.
            currentIteration = 0;
            simpleIterationProgress = 1.0;
        }
        else if (phase == AnimationPhase.After
                 && IsAtEndOfLastIteration(overallProgress, timing.IterationStart, timing.Iterations))
        {
            // §4.7: at the exact end of the final iteration, progress = 1
            // and currentIteration = the last iteration index.
            var rawIter = timing.IterationStart + timing.Iterations;
            currentIteration = (int)Math.Max(0, Math.Ceiling(rawIter) - 1);
            simpleIterationProgress = 1.0;
        }
        else
        {
            currentIteration = (int)Math.Floor(overallProgress);
            simpleIterationProgress = overallProgress - Math.Floor(overallProgress);
        }

        // §4.8 — directed progress: flip for reverse/alternate iterations.
        // https://www.w3.org/TR/web-animations-1/#calculating-the-directed-progress
        bool shouldReverse = IsCurrentIterationReverse(timing.Direction, currentIteration);
        double directedProgress = shouldReverse
            ? 1.0 - simpleIterationProgress
            : simpleIterationProgress;

        // §4.8.5 — transformed progress: apply the effect's easing function.
        // https://www.w3.org/TR/web-animations-1/#calculating-the-transformed-progress
        double transformedProgress = timing.Easing.Evaluate(directedProgress);

        return new ComputedTiming(phase, currentIteration, transformedProgress);
    }

    // Returns true when overallProgress lands exactly at the end boundary of
    // the last iteration (i.e. the progress value is an integer at the end of
    // the active phase). Matching the spec's edge-case rule in §4.7.
    private static bool IsAtEndOfLastIteration(
        double overallProgress, double iterationStart, double iterations)
    {
        if (iterations <= 0)
        {
            return false;
        }

        var endBoundary = iterationStart + iterations;
        // overallProgress == endBoundary and the fractional part is zero
        // (or exactly lands on an integer), meaning we are exactly at the end.
        return Math.Abs(overallProgress - endBoundary) < 1e-10
               && Math.Abs(overallProgress - Math.Round(overallProgress)) < 1e-10;
    }

    // §4.8 — whether the current iteration plays in reverse.
    private static bool IsCurrentIterationReverse(PlaybackDirection direction, int currentIteration)
        => direction switch
        {
            PlaybackDirection.Normal => false,
            PlaybackDirection.Reverse => true,
            // alternate: even iterations (0, 2, 4…) forward; odd reverse.
            PlaybackDirection.Alternate => (currentIteration & 1) == 1,
            // alternate-reverse: even iterations reverse; odd forward.
            PlaybackDirection.AlternateReverse => (currentIteration & 1) == 0,
            _ => false,
        };
}
