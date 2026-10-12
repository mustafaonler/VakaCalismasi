namespace AtmCash.Domain.Cash;

/// <summary>
/// Finds the note breakdown that pays an amount with the fewest notes, given a limited
/// number of notes per denomination (bounded change-making).
/// </summary>
/// <remarks>
/// Greedy selection (largest note first) is not optimal with limited stock, e.g. 110 from
/// 100x3, 50x1, 20x1 or 200 from 100x1, 50x4, 20x5. Instead a dynamic program computes,
/// for every payable amount up to the total stock, the minimum number of notes. That single
/// table answers the exact request as well as the lower and upper alternatives.
/// All denominations are multiples of 10, so amounts are indexed in units of 10. Worst case
/// is 4 cassettes x 100 notes x 500 = 200,000 (20,000 cells), each cell trying up to
/// <c>count + 1</c> note counts per denomination: a few million operations.
/// </remarks>
public static class NoteSelector
{
    private const int Unit = 10;
    private const int Unreachable = int.MaxValue;

    /// <param name="available">Notes available per denomination.</param>
    /// <param name="requestedAmount">Amount the customer asked for.</param>
    /// <param name="upperAmountCap">
    /// Highest amount the upper alternative may have (e.g. the customer's remaining daily limit).
    /// <c>null</c> means no cap.
    /// </param>
    public static WithdrawalOptions Select(
        IReadOnlyDictionary<int, int> available,
        int requestedAmount,
        int? upperAmountCap = null)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedAmount);
        foreach (var (denomination, count) in available)
        {
            if (!Denominations.IsValid(denomination))
            {
                throw new ArgumentException($"Unsupported denomination {denomination}.", nameof(available));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(available));
        }

        var stock = available
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Key)
            .Select(kv => (Units: kv.Key / Unit, Count: kv.Value))
            .ToArray();

        var maxUnits = stock.Sum(s => s.Units * s.Count);

        // minNotes[a]: fewest notes paying a * Unit with the denominations processed so far.
        // taken[i][a]: how many notes of stock[i] that optimum uses, for reconstruction.
        var minNotes = new int[maxUnits + 1];
        Array.Fill(minNotes, Unreachable);
        minNotes[0] = 0;
        var taken = new int[stock.Length][];

        for (var i = 0; i < stock.Length; i++)
        {
            var (units, count) = stock[i];
            var next = new int[maxUnits + 1];
            Array.Fill(next, Unreachable);
            taken[i] = new int[maxUnits + 1];

            for (var a = 0; a <= maxUnits; a++)
            {
                var maxK = Math.Min(count, a / units);
                // Strict '<' with ascending k keeps the fewest notes of the smaller denomination on ties.
                for (var k = 0; k <= maxK; k++)
                {
                    var previous = minNotes[a - k * units];
                    if (previous != Unreachable && previous + k < next[a])
                    {
                        next[a] = previous + k;
                        taken[i][a] = k;
                    }
                }
            }

            minNotes = next;
        }

        bool Payable(int units) => units > 0 && minNotes[units] != Unreachable;

        NoteBreakdown Build(int units)
        {
            var notes = new List<NoteLine>();
            for (var i = stock.Length - 1; i >= 0; i--)
            {
                var k = taken[i][units];
                if (k > 0)
                {
                    notes.Add(new NoteLine(stock[i].Units * Unit, k));
                    units -= k * stock[i].Units;
                }
            }

            notes.Reverse();
            return new NoteBreakdown(notes.Sum(n => n.Denomination * n.Count), notes);
        }

        if (requestedAmount % Unit == 0
            && requestedAmount / Unit <= maxUnits
            && Payable(requestedAmount / Unit))
        {
            return new WithdrawalOptions(Build(requestedAmount / Unit), null, null);
        }

        NoteBreakdown? lower = null;
        for (var a = Math.Min((requestedAmount - 1) / Unit, maxUnits); a > 0; a--)
        {
            if (Payable(a))
            {
                lower = Build(a);
                break;
            }
        }

        NoteBreakdown? upper = null;
        var upperLimitUnits = Math.Min(maxUnits, (upperAmountCap ?? int.MaxValue) / Unit);
        for (var a = requestedAmount / Unit + 1; a <= upperLimitUnits; a++)
        {
            if (Payable(a))
            {
                upper = Build(a);
                break;
            }
        }

        return new WithdrawalOptions(null, lower, upper);
    }
}
