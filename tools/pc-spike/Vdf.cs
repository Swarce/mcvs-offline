using System;
using System.Collections.Generic;
using System.Text;

namespace McvsOffline.PcSpike
{
    /// <summary>One node of a Valve KeyValues text file: a string value or a block of children.</summary>
    public sealed class VdfNode
    {
        public VdfNode(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public VdfNode(string key, List<VdfNode> children)
        {
            Key = key;
            Children = children;
        }

        public string Key { get; }

        /// <summary>The value of a "key" "value" pair; null for a block.</summary>
        public string Value { get; }

        /// <summary>The children of a "key" { ... } block; null for a value.</summary>
        public List<VdfNode> Children { get; }

        public bool IsBlock => Children != null;

        /// <summary>The first child named <paramref name="key"/> (Valve compares keys case-insensitively), or null.</summary>
        public VdfNode this[string key]
        {
            get
            {
                if (Children == null) return null;
                foreach (VdfNode child in Children)
                {
                    if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase)) return child;
                }
                return null;
            }
        }

        /// <summary>The string value of the child <paramref name="key"/>, or null.</summary>
        public string Get(string key)
        {
            VdfNode child = this[key];
            return child == null ? null : child.Value;
        }
    }

    /// <summary>
    /// Reads Valve's KeyValues text format as Steam writes it (<c>steamapps/libraryfolders.vdf</c>,
    /// <c>appmanifest_*.acf</c>): quoted or bare tokens, <c>{ }</c> blocks, <c>//</c> comments, the escapes <c>\\ \" \n \t</c>,
    /// and <c>[$WIN32]</c>-style conditions (skipped). The root is a block holding the top-level pairs. Malformed input
    /// throws <see cref="FormatException"/>.
    /// </summary>
    public static class Vdf
    {
        public static VdfNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int position = 0;
            if (text.Length > 0 && text[0] == '﻿') position = 1;
            List<VdfNode> top = ParseBlock(text, ref position, false);
            return new VdfNode("", top);
        }

        private static List<VdfNode> ParseBlock(string text, ref int position, bool nested)
        {
            var nodes = new List<VdfNode>();
            while (true)
            {
                Token key = Next(text, ref position);
                if (key.Kind == TokenKind.End)
                {
                    if (nested) throw new FormatException("unexpected end of file inside a block");
                    return nodes;
                }
                if (key.Kind == TokenKind.Close)
                {
                    if (!nested) throw new FormatException("unbalanced '}' at offset " + key.Offset);
                    return nodes;
                }
                if (key.Kind != TokenKind.String) throw new FormatException("expected a key at offset " + key.Offset);

                Token value = Next(text, ref position);
                if (value.Kind == TokenKind.Condition) value = Next(text, ref position);
                if (value.Kind == TokenKind.Open)
                {
                    nodes.Add(new VdfNode(key.Text, ParseBlock(text, ref position, true)));
                }
                else if (value.Kind == TokenKind.String)
                {
                    nodes.Add(new VdfNode(key.Text, value.Text));
                }
                else
                {
                    throw new FormatException("expected a value or '{' after \"" + key.Text + "\" at offset " + value.Offset);
                }
                SkipCondition(text, ref position);
            }
        }

        private enum TokenKind { End, Open, Close, String, Condition }

        private struct Token
        {
            public TokenKind Kind;
            public string Text;
            public int Offset;
        }

        /// <summary>Skips one optional trailing "[$COND]" after a pair.</summary>
        private static void SkipCondition(string text, ref int position)
        {
            int saved = position;
            Token token = Next(text, ref position);
            if (token.Kind != TokenKind.Condition) position = saved;
        }

        private static Token Next(string text, ref int position)
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (char.IsWhiteSpace(c))
                {
                    position++;
                    continue;
                }
                if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n') position++;
                    continue;
                }
                break;
            }
            var token = new Token { Offset = position };
            if (position >= text.Length)
            {
                token.Kind = TokenKind.End;
                return token;
            }
            char first = text[position];
            if (first == '{')
            {
                position++;
                token.Kind = TokenKind.Open;
                return token;
            }
            if (first == '}')
            {
                position++;
                token.Kind = TokenKind.Close;
                return token;
            }
            if (first == '[')
            {
                int end = text.IndexOf(']', position);
                if (end < 0) throw new FormatException("unterminated condition at offset " + position);
                position = end + 1;
                token.Kind = TokenKind.Condition;
                return token;
            }
            var sb = new StringBuilder();
            if (first == '"')
            {
                position++;
                while (true)
                {
                    if (position >= text.Length) throw new FormatException("unterminated string at offset " + token.Offset);
                    char c = text[position++];
                    if (c == '"') break;
                    if (c == '\\' && position < text.Length)
                    {
                        char e = text[position++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append('\\').Append(e); break;
                        }
                        continue;
                    }
                    sb.Append(c);
                }
            }
            else
            {
                while (position < text.Length)
                {
                    char c = text[position];
                    if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"') break;
                    sb.Append(c);
                    position++;
                }
            }
            token.Kind = TokenKind.String;
            token.Text = sb.ToString();
            return token;
        }
    }
}
