// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Evaluates CEL-subset condition expressions used in DAG node <c>condition</c> fields (NPS-5 §3.1.5).
/// <para>
/// Supported syntax:
/// <list type="bullet">
///   <item>Comparison: <c>$.node.field &gt; 0.7</c>, <c>$.node.status == "ok"</c>, <c>$.n.x != null</c></item>
///   <item>Boolean logic: <c>&amp;&amp;</c>, <c>||</c>, <c>!</c></item>
///   <item>Grouping: <c>( expr )</c></item>
///   <item>Literals: numbers, quoted strings, <c>true</c>, <c>false</c>, <c>null</c></item>
///   <item>JSONPath access: <c>$.node_id.field.sub</c> (uses <see cref="NopInputMapper.Resolve"/>)</item>
/// </list>
/// </para>
/// </summary>
public static class NopConditionEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="condition"/> in the context of completed node results.
    /// Returns <c>true</c> if the node should execute; <c>false</c> if it should be skipped.
    /// </summary>
    /// <exception cref="NopConditionException">Thrown for syntax errors or unresolvable paths.</exception>
    public static bool Evaluate(string condition, IReadOnlyDictionary<string, JsonElement> context)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        try
        {
            var tokens = Tokenize(condition.Trim());
            var parser = new ConditionParser(tokens, context);
            return parser.ParseOrExpr();
        }
        catch (NopConditionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NopConditionException($"Condition evaluation error: {ex.Message}", condition);
        }
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private enum TokenKind
    {
        DollarPath,   // $.node.field
        Number,       // 42 | 3.14
        String,       // "text"
        True, False, Null,
        Gt, Gte, Lt, Lte, Eq, Neq,
        And, Or, Not,
        LParen, RParen,
        Eof,
    }

    private readonly record struct Token(TokenKind Kind, string Raw);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(input[i])) { i++; continue; }

            // Dollar path
            if (input[i] == '$' && i + 1 < input.Length && input[i + 1] == '.')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '.' || input[i] == '$'))
                    i++;
                tokens.Add(new Token(TokenKind.DollarPath, input[start..i]));
                continue;
            }

            // String literal
            if (input[i] == '"')
            {
                int start = i++;
                while (i < input.Length && input[i] != '"') i++;
                i++; // closing quote
                tokens.Add(new Token(TokenKind.String, input[(start + 1)..(i - 1)]));
                continue;
            }

            // Number
            if (char.IsDigit(input[i]) || (input[i] == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                int start = i;
                if (input[i] == '-') i++;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, input[start..i]));
                continue;
            }

            // Operators
            if (input[i] == '>' && i + 1 < input.Length && input[i + 1] == '=') { tokens.Add(new Token(TokenKind.Gte, ">=")); i += 2; continue; }
            if (input[i] == '<' && i + 1 < input.Length && input[i + 1] == '=') { tokens.Add(new Token(TokenKind.Lte, "<=")); i += 2; continue; }
            if (input[i] == '=' && i + 1 < input.Length && input[i + 1] == '=') { tokens.Add(new Token(TokenKind.Eq,  "==")); i += 2; continue; }
            if (input[i] == '!' && i + 1 < input.Length && input[i + 1] == '=') { tokens.Add(new Token(TokenKind.Neq, "!=")); i += 2; continue; }
            if (input[i] == '&' && i + 1 < input.Length && input[i + 1] == '&') { tokens.Add(new Token(TokenKind.And, "&&")); i += 2; continue; }
            if (input[i] == '|' && i + 1 < input.Length && input[i + 1] == '|') { tokens.Add(new Token(TokenKind.Or,  "||")); i += 2; continue; }
            if (input[i] == '>') { tokens.Add(new Token(TokenKind.Gt,  ">")); i++; continue; }
            if (input[i] == '<') { tokens.Add(new Token(TokenKind.Lt,  "<")); i++; continue; }
            if (input[i] == '!') { tokens.Add(new Token(TokenKind.Not, "!")); i++; continue; }
            if (input[i] == '(') { tokens.Add(new Token(TokenKind.LParen, "(")); i++; continue; }
            if (input[i] == ')') { tokens.Add(new Token(TokenKind.RParen, ")")); i++; continue; }

            // Keywords: true, false, null
            if (char.IsLetter(input[i]))
            {
                int start = i;
                while (i < input.Length && char.IsLetterOrDigit(input[i])) i++;
                var kw = input[start..i];
                tokens.Add(kw switch
                {
                    "true"  => new Token(TokenKind.True,  "true"),
                    "false" => new Token(TokenKind.False, "false"),
                    "null"  => new Token(TokenKind.Null,  "null"),
                    _       => throw new NopConditionException($"Unknown token '{kw}'.", input),
                });
                continue;
            }

            throw new NopConditionException($"Unexpected character '{input[i]}' at position {i}.", input);
        }

        tokens.Add(new Token(TokenKind.Eof, ""));
        return tokens;
    }

    // ── Recursive-descent parser ──────────────────────────────────────────────

    private sealed class ConditionParser(List<Token> tokens, IReadOnlyDictionary<string, JsonElement> context)
    {
        private int _pos;

        private Token Current => tokens[_pos];
        private Token Consume() => tokens[_pos++];

        // or_expr := and_expr ('||' and_expr)*
        public bool ParseOrExpr()
        {
            var left = ParseAndExpr();
            while (Current.Kind == TokenKind.Or) { Consume(); left = left || ParseAndExpr(); }
            return left;
        }

        // and_expr := not_expr ('&&' not_expr)*
        private bool ParseAndExpr()
        {
            var left = ParseNotExpr();
            while (Current.Kind == TokenKind.And) { Consume(); left = left && ParseNotExpr(); }
            return left;
        }

        // not_expr := '!' primary | primary
        private bool ParseNotExpr()
        {
            if (Current.Kind == TokenKind.Not) { Consume(); return !ParseNotExpr(); }
            return ParseComparison();
        }

        // comparison := value (op value)?  |  '(' or_expr ')'  |  true | false
        private bool ParseComparison()
        {
            if (Current.Kind == TokenKind.LParen)
            {
                Consume(); // '('
                var inner = ParseOrExpr();
                if (Current.Kind != TokenKind.RParen)
                    throw new NopConditionException("Expected ')'.", "");
                Consume();
                return inner;
            }

            if (Current.Kind == TokenKind.True)  { Consume(); return true;  }
            if (Current.Kind == TokenKind.False) { Consume(); return false; }

            var lhs = ParseValue();

            var opKind = Current.Kind;
            if (!IsComparisonOp(opKind)) return AsTruthy(lhs);

            Consume(); // operator
            var rhs = ParseValue();
            return Compare(lhs, opKind, rhs);
        }

        private static bool IsComparisonOp(TokenKind k) =>
            k is TokenKind.Gt or TokenKind.Gte or TokenKind.Lt or TokenKind.Lte
              or TokenKind.Eq or TokenKind.Neq;

        // value := dollar_path | number | string | null | true | false
        private (TokenKind Kind, object? Value) ParseValue()
        {
            var tok = Current;
            Consume();
            return tok.Kind switch
            {
                TokenKind.DollarPath => (TokenKind.DollarPath, ResolvePath(tok.Raw)),
                TokenKind.Number     => (TokenKind.Number, double.Parse(tok.Raw)),
                TokenKind.String     => (TokenKind.String, tok.Raw),
                TokenKind.True       => (TokenKind.True,  true),
                TokenKind.False      => (TokenKind.False, false),
                TokenKind.Null       => (TokenKind.Null,  null),
                _                   => throw new NopConditionException($"Expected a value, got '{tok.Raw}'.", ""),
            };
        }

        private object? ResolvePath(string path)
        {
            var element = NopInputMapper.Resolve(path, context);
            if (element is null) return null;
            return element.Value.ValueKind switch
            {
                JsonValueKind.Number  => element.Value.GetDouble(),
                JsonValueKind.String  => element.Value.GetString(),
                JsonValueKind.True    => true,
                JsonValueKind.False   => false,
                JsonValueKind.Null    => null,
                _                     => element.Value.GetRawText(), // object/array as string
            };
        }

        private static bool AsTruthy((TokenKind Kind, object? Value) v) =>
            v.Value switch
            {
                bool b   => b,
                double d => d != 0,
                string s => !string.IsNullOrEmpty(s),
                null     => false,
                _        => true,
            };

        private static bool Compare((TokenKind, object?) lhs, TokenKind op, (TokenKind, object?) rhs)
        {
            // Null comparisons
            if (op == TokenKind.Eq)  return Equals(lhs.Item2, rhs.Item2);
            if (op == TokenKind.Neq) return !Equals(lhs.Item2, rhs.Item2);
            if (lhs.Item2 is null || rhs.Item2 is null) return false;

            // Numeric comparisons
            if (lhs.Item2 is double ld && rhs.Item2 is double rd)
            {
                return op switch
                {
                    TokenKind.Gt  => ld >  rd,
                    TokenKind.Gte => ld >= rd,
                    TokenKind.Lt  => ld <  rd,
                    TokenKind.Lte => ld <= rd,
                    _             => false,
                };
            }

            // String comparisons
            if (lhs.Item2 is string ls && rhs.Item2 is string rs)
            {
                int cmp = string.Compare(ls, rs, StringComparison.Ordinal);
                return op switch
                {
                    TokenKind.Gt  => cmp > 0,
                    TokenKind.Gte => cmp >= 0,
                    TokenKind.Lt  => cmp < 0,
                    TokenKind.Lte => cmp <= 0,
                    _             => false,
                };
            }

            return false;
        }
    }
}

/// <summary>Thrown when a condition expression cannot be parsed or evaluated.</summary>
public sealed class NopConditionException : Exception
{
    public NopConditionException(string message, string expression)
        : base($"{message}  Expression: «{expression}»") { }
}
