namespace UttArena.Contracts;

public sealed record ProtocolValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class ProtocolValidationException : Exception
{
    public ProtocolValidationException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public static class ProtocolValidation
{
    public static ProtocolValidationResult Validate(MatchStartMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var errors = new List<string>();

        ValidateHeader(message.ProtocolVersion, message.Type, ProtocolConstants.MatchStartType, errors);
        RequireText(message.MatchId, "matchId", errors);
        ValidateMark(message.BotMark, "botMark", errors);
        ValidateOpponent(message.Opponent, errors);
        ValidateRuleset(message.Ruleset, errors);
        ValidateTimingControl(message.Timing, errors);

        return new(errors);
    }

    public static ProtocolValidationResult Validate(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();

        ValidateHeader(request.ProtocolVersion, request.Type, ProtocolConstants.MoveRequestType, errors);
        RequireText(request.RequestId, "requestId", errors);
        RequireText(request.MatchId, "matchId", errors);
        ValidateMark(request.BotMark, "botMark", errors);
        ValidateBoard(
            request.Board,
            request.BotMark,
            request.History,
            request.LegalMoves,
            request.Variant,
            request.SpecialAvailability,
            request.DoubleMovePending,
            request.LegalSpecialMoves,
            errors);
        ValidateOpponent(request.Opponent, errors);
        ValidateRuleset(request.Ruleset, errors);
        ValidateMoveTiming(request.Timing, errors);

        return new(errors);
    }

    public static ProtocolValidationResult Validate(MoveResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var errors = new List<string>();

        ValidateHeader(response.ProtocolVersion, response.Type, ProtocolConstants.MoveResponseType, errors);
        RequireText(response.RequestId, "requestId", errors);
        ValidatePosition(response.Move, "move", errors);
        // Banter is advisory: invalid banter is stripped by the arena, not a protocol failure.

        return new(errors);
    }

    /// <summary>
    /// Returns a sanitized banter line, or null when the value should be dropped.
    /// </summary>
    public static string? NormalizeBanter(string? banter)
    {
        if (string.IsNullOrWhiteSpace(banter))
        {
            return null;
        }

        var trimmed = banter.Trim();
        if (trimmed.Length > ProtocolLimits.MaxBanterLength)
        {
            return null;
        }

        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return null;
        }

        return trimmed;
    }

    public static ProtocolValidationResult ValidateMove(
        MoveRequest request,
        BoardPosition move,
        bool useSpecial = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        ValidatePosition(move, "move", errors);

        var allowedMoves = useSpecial ? request.LegalSpecialMoves : request.LegalMoves;
        if (move is not null &&
            allowedMoves is not null &&
            !allowedMoves.Contains(move))
        {
            errors.Add(useSpecial
                ? "move must be one of legalSpecialMoves when useSpecial is true."
                : "move must be one of legalMoves.");
        }
        else if (useSpecial && allowedMoves is null)
        {
            errors.Add("useSpecial is not available for this request.");
        }

        return new(errors);
    }

    public static void EnsureValid(MatchStartMessage message) =>
        ThrowIfInvalid(Validate(message));

    public static void EnsureValid(MoveRequest request) =>
        ThrowIfInvalid(Validate(request));

    public static void EnsureValid(MoveResponse response) =>
        ThrowIfInvalid(Validate(response));

    public static void EnsureValidMove(
        MoveRequest request,
        BoardPosition move,
        bool useSpecial = false) =>
        ThrowIfInvalid(ValidateMove(request, move, useSpecial));

    private static void ThrowIfInvalid(ProtocolValidationResult result)
    {
        if (!result.IsValid)
        {
            throw new ProtocolValidationException(result.Errors);
        }
    }

    private static void ValidateHeader(
        int version,
        string type,
        string expectedType,
        ICollection<string> errors)
    {
        if (version != ProtocolConstants.Version)
        {
            errors.Add($"protocolVersion must be {ProtocolConstants.Version}.");
        }

        if (!string.Equals(type, expectedType, StringComparison.Ordinal))
        {
            errors.Add($"type must be '{expectedType}'.");
        }
    }

    private static void ValidateBoard(
        BoardState board,
        PlayerMark botMark,
        IReadOnlyList<MoveRecord> history,
        IReadOnlyList<BoardPosition> legalMoves,
        ProtocolGameVariant variant,
        SpecialAvailability? specialAvailability,
        bool doubleMovePending,
        IReadOnlyList<BoardPosition>? legalSpecialMoves,
        ICollection<string> errors)
    {
        if (board is null)
        {
            errors.Add("board is required.");
            return;
        }

        if (board.Cells is null || board.Cells.Count != ProtocolConstants.CellCount)
        {
            errors.Add($"board.cells must contain exactly {ProtocolConstants.CellCount} row-major cells.");
        }
        else
        {
            for (var i = 0; i < board.Cells.Count; i++)
            {
                if (!Enum.IsDefined(board.Cells[i]))
                {
                    errors.Add($"board.cells[{i}] is invalid.");
                }
            }
        }

        ValidateLocalBoards(board.LocalBoards, errors);

        if (board.ActiveLocalBoard is < 0 or >= ProtocolConstants.LocalBoardCount)
        {
            errors.Add($"board.activeLocalBoard must be null or between 0 and {ProtocolConstants.LocalBoardCount - 1}.");
        }

        ValidateMark(board.CurrentTurn, "board.currentTurn", errors);
        if (board.CurrentTurn != botMark)
        {
            errors.Add("board.currentTurn must equal botMark.");
        }

        if (board.MoveNumber < 0 || board.MoveNumber > ProtocolConstants.CellCount)
        {
            errors.Add($"board.moveNumber must be between 0 and {ProtocolConstants.CellCount}.");
        }

        ValidateHistory(history, board, variant, errors);
        ValidateLegalMoves(legalMoves, board, errors);
        ValidateSpecialMoves(
            legalSpecialMoves,
            board,
            variant,
            specialAvailability,
            doubleMovePending,
            botMark,
            errors);
    }

    private static void ValidateLocalBoards(
        IReadOnlyList<LocalBoardState> localBoards,
        ICollection<string> errors)
    {
        if (localBoards is null || localBoards.Count != ProtocolConstants.LocalBoardCount)
        {
            errors.Add($"board.localBoards must contain exactly {ProtocolConstants.LocalBoardCount} entries.");
            return;
        }

        var indices = new HashSet<int>();
        for (var i = 0; i < localBoards.Count; i++)
        {
            var localBoard = localBoards[i];
            if (localBoard is null)
            {
                errors.Add($"board.localBoards[{i}] is required.");
                continue;
            }

            if (localBoard.Index is < 0 or >= ProtocolConstants.LocalBoardCount)
            {
                errors.Add($"board.localBoards[{i}].index is out of range.");
            }
            else if (!indices.Add(localBoard.Index))
            {
                errors.Add($"board.localBoards contains duplicate index {localBoard.Index}.");
            }

            if (!Enum.IsDefined(localBoard.Status))
            {
                errors.Add($"board.localBoards[{i}].status is invalid.");
            }
        }
    }

    private static void ValidateHistory(
        IReadOnlyList<MoveRecord> history,
        BoardState board,
        ProtocolGameVariant variant,
        ICollection<string> errors)
    {
        if (history is null)
        {
            errors.Add("history is required.");
            return;
        }

        if (history.Count != board.MoveNumber)
        {
            errors.Add("history count must equal board.moveNumber.");
        }

        var occupied = new HashSet<BoardPosition>();
        for (var i = 0; i < history.Count; i++)
        {
            var move = history[i];
            if (move is null)
            {
                errors.Add($"history[{i}] is required.");
                continue;
            }

            if (move.Ply != i + 1)
            {
                errors.Add($"history[{i}].ply must be {i + 1}.");
            }

            ValidateMark(move.Mark, $"history[{i}].mark", errors);
            var expectedMark = i % 2 == 0 ? PlayerMark.X : PlayerMark.O;
            if (variant != ProtocolGameVariant.DoubleMove && move.Mark != expectedMark)
            {
                errors.Add($"history[{i}].mark must be {expectedMark}.");
            }

            ValidatePosition(move.Position, $"history[{i}].position", errors);
            if (move.Position is not null && !occupied.Add(move.Position))
            {
                errors.Add($"history[{i}].position was already played.");
            }

            if (move.Position is not null &&
                IsPositionValid(move.Position) &&
                board.Cells is not null &&
                board.Cells.Count == ProtocolConstants.CellCount)
            {
                var expectedCell = move.Mark == PlayerMark.X ? CellState.X : CellState.O;
                var actualCell = board.Cells[ToCellIndex(move.Position)];
                if (actualCell != expectedCell)
                {
                    errors.Add($"board.cells does not contain history[{i}].mark at its position.");
                }
            }
        }
    }

    private static void ValidateLegalMoves(
        IReadOnlyList<BoardPosition> legalMoves,
        BoardState board,
        ICollection<string> errors)
    {
        if (legalMoves is null || legalMoves.Count == 0)
        {
            errors.Add("legalMoves must contain at least one move.");
            return;
        }

        var uniqueMoves = new HashSet<BoardPosition>();
        for (var i = 0; i < legalMoves.Count; i++)
        {
            var move = legalMoves[i];
            ValidatePosition(move, $"legalMoves[{i}]", errors);
            if (move is null || !IsPositionValid(move))
            {
                continue;
            }

            if (!uniqueMoves.Add(move))
            {
                errors.Add($"legalMoves contains duplicate ({move.Row}, {move.Column}).");
            }

            if (board.Cells is not null &&
                board.Cells.Count == ProtocolConstants.CellCount &&
                board.Cells[ToCellIndex(move)] != CellState.Empty)
            {
                errors.Add($"legalMoves[{i}] points to an occupied cell.");
            }

            var localBoardIndex = (move.Row / ProtocolConstants.LocalBoardSize * ProtocolConstants.LocalBoardSize)
                + (move.Column / ProtocolConstants.LocalBoardSize);

            if (board.ActiveLocalBoard is int activeLocalBoard && localBoardIndex != activeLocalBoard)
            {
                errors.Add($"legalMoves[{i}] is outside board.activeLocalBoard.");
            }

            var localBoard = board.LocalBoards?.FirstOrDefault(candidate => candidate.Index == localBoardIndex);
            if (localBoard is not null && localBoard.Status != LocalBoardStatus.Open)
            {
                errors.Add($"legalMoves[{i}] is in a closed local board.");
            }
        }
    }

    private static void ValidateSpecialMoves(
        IReadOnlyList<BoardPosition>? legalSpecialMoves,
        BoardState board,
        ProtocolGameVariant variant,
        SpecialAvailability? specialAvailability,
        bool doubleMovePending,
        PlayerMark botMark,
        ICollection<string> errors)
    {
        if (!Enum.IsDefined(variant))
        {
            errors.Add("variant is invalid.");
            return;
        }

        if (variant == ProtocolGameVariant.Standard)
        {
            if (specialAvailability is not null || doubleMovePending || legalSpecialMoves is not null)
            {
                errors.Add("special fields are only valid for Wildcard or DoubleMove.");
            }
            return;
        }

        if (specialAvailability is null)
        {
            errors.Add("specialAvailability is required for a special variant.");
        }

        if (doubleMovePending && variant != ProtocolGameVariant.DoubleMove)
        {
            errors.Add("doubleMovePending is only valid for DoubleMove.");
        }

        var available = specialAvailability?.IsAvailable(botMark) == true;
        if (legalSpecialMoves is null)
        {
            return;
        }

        if (!available)
        {
            errors.Add("legalSpecialMoves requires the current player's special to be available.");
        }

        if (doubleMovePending)
        {
            errors.Add("legalSpecialMoves must be omitted while a double move is pending.");
        }

        if (legalSpecialMoves.Count == 0)
        {
            errors.Add("legalSpecialMoves must be omitted instead of empty.");
            return;
        }

        var uniqueMoves = new HashSet<BoardPosition>();
        for (var i = 0; i < legalSpecialMoves.Count; i++)
        {
            var move = legalSpecialMoves[i];
            ValidatePosition(move, $"legalSpecialMoves[{i}]", errors);
            if (move is null || !IsPositionValid(move))
            {
                continue;
            }

            if (!uniqueMoves.Add(move))
            {
                errors.Add($"legalSpecialMoves contains duplicate ({move.Row}, {move.Column}).");
            }

            if (board.Cells is not null &&
                board.Cells.Count == ProtocolConstants.CellCount &&
                board.Cells[ToCellIndex(move)] != CellState.Empty)
            {
                errors.Add($"legalSpecialMoves[{i}] points to an occupied cell.");
            }

            var localBoardIndex = (move.Row / ProtocolConstants.LocalBoardSize * ProtocolConstants.LocalBoardSize)
                + (move.Column / ProtocolConstants.LocalBoardSize);
            var localBoard = board.LocalBoards?.FirstOrDefault(candidate => candidate.Index == localBoardIndex);
            if (localBoard is not null && localBoard.Status != LocalBoardStatus.Open)
            {
                errors.Add($"legalSpecialMoves[{i}] is in a closed local board.");
            }

            if (variant == ProtocolGameVariant.Wildcard &&
                board.ActiveLocalBoard is int activeLocalBoard &&
                localBoardIndex == activeLocalBoard)
            {
                errors.Add($"legalSpecialMoves[{i}] does not bypass board.activeLocalBoard.");
            }

            if (variant == ProtocolGameVariant.DoubleMove &&
                board.ActiveLocalBoard is int forcedBoard &&
                localBoardIndex != forcedBoard)
            {
                errors.Add($"legalSpecialMoves[{i}] is outside board.activeLocalBoard.");
            }
        }
    }

    private static void ValidateRuleset(RulesetDescriptor ruleset, ICollection<string> errors)
    {
        if (ruleset is null)
        {
            errors.Add("ruleset is required.");
            return;
        }

        if (!string.Equals(ruleset.Id, ProtocolConstants.StandardRulesetId, StringComparison.Ordinal))
        {
            errors.Add($"ruleset.id must be '{ProtocolConstants.StandardRulesetId}'.");
        }

        if (ruleset.Version != ProtocolConstants.Version)
        {
            errors.Add($"ruleset.version must be {ProtocolConstants.Version}.");
        }

        if (ruleset.BoardSize != ProtocolConstants.BoardSize ||
            ruleset.LocalBoardSize != ProtocolConstants.LocalBoardSize)
        {
            errors.Add($"ruleset board sizes must be {ProtocolConstants.BoardSize} and {ProtocolConstants.LocalBoardSize}.");
        }

        if (!ruleset.SendMoveToLocalBoard || !ruleset.FreeMoveWhenTargetBoardClosed)
        {
            errors.Add("ruleset flags must describe the standard v1 rules.");
        }
    }

    private static void ValidateTimingControl(TimingControl timing, ICollection<string> errors)
    {
        if (timing is null)
        {
            errors.Add("timing is required.");
            return;
        }

        ValidateTimingValues(timing.MoveTimeoutMs, timing.InitialBankMs, timing.IncrementMs, errors);
    }

    private static void ValidateMoveTiming(MoveTiming timing, ICollection<string> errors)
    {
        if (timing is null)
        {
            errors.Add("timing is required.");
            return;
        }

        ValidateTimingValues(timing.MoveTimeoutMs, timing.RemainingBankMs, timing.IncrementMs, errors);
        if (timing.DeadlineUtc == default || timing.DeadlineUtc.Offset != TimeSpan.Zero)
        {
            errors.Add("timing.deadlineUtc must be a non-default UTC timestamp.");
        }
    }

    private static void ValidateTimingValues(
        int moveTimeoutMs,
        long? bankMs,
        int incrementMs,
        ICollection<string> errors)
    {
        if (moveTimeoutMs <= 0)
        {
            errors.Add("timing.moveTimeoutMs must be positive.");
        }

        if (bankMs is < 0)
        {
            errors.Add("timing bank milliseconds cannot be negative.");
        }

        if (incrementMs < 0)
        {
            errors.Add("timing.incrementMs cannot be negative.");
        }
    }

    private static void ValidateOpponent(OpponentMetadata opponent, ICollection<string> errors)
    {
        if (opponent is null)
        {
            errors.Add("opponent is required.");
            return;
        }

        RequireText(opponent.BotId, "opponent.botId", errors);
        RequireText(opponent.DisplayName, "opponent.displayName", errors);
    }

    private static void ValidatePosition(
        BoardPosition position,
        string path,
        ICollection<string> errors)
    {
        if (position is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (!IsPositionValid(position))
        {
            errors.Add($"{path} row and column must each be between 0 and {ProtocolConstants.BoardSize - 1}.");
        }
    }

    private static bool IsPositionValid(BoardPosition position) =>
        position.Row is >= 0 and < ProtocolConstants.BoardSize &&
        position.Column is >= 0 and < ProtocolConstants.BoardSize;

    private static int ToCellIndex(BoardPosition position) =>
        (position.Row * ProtocolConstants.BoardSize) + position.Column;

    private static void ValidateMark(PlayerMark mark, string path, ICollection<string> errors)
    {
        if (!Enum.IsDefined(mark))
        {
            errors.Add($"{path} is invalid.");
        }
    }

    private static void RequireText(string value, string path, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required.");
        }
    }
}
