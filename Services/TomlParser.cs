using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>최소 TOML 파서 ( codex config 용 ).
/// <c>[mcp_servers.NAME]</c> 같은 section header + key=value 만 지원.
/// 지원 타입: string, integer, float, boolean, array(같은 타입), inline table, nested table.
/// 그 외 TOML 기능은 만나면 string 으로 들어온다(최선).</summary>
public sealed class TomlParser
{
    private readonly string _src;
    private int _pos;

    public TomlParser(string source) { _src = source ?? ""; _pos = 0; }

    public TomlTable Parse()
    {
        var root = new TomlTable("");
        while (_pos < _src.Length)
        {
            SkipWhitespaceAndNewlines();
            if (_pos >= _src.Length) break;
            if (_src[_pos] == '#') { SkipLine(); continue; }
            if (_src[_pos] == '[')
            {
                // [name] or [name.subname]
                var (name, isArray) = ParseSectionHeader();
                if (isArray) { /* [[array]] 미지원 — 무시 */ continue; }
                var table = EnsureTable(root, name);
                ParseTableBody(table);
            }
            else
            {
                // 최상위 key=value (테이블 헤더 없이)
                ParseKeyValue(root);
            }
        }
        return root;
    }

    private void ParseTableBody(TomlTable table)
    {
        while (_pos < _src.Length)
        {
            SkipWhitespaceAndNewlines();
            if (_pos >= _src.Length) break;
            if (_src[_pos] == '#') { SkipLine(); continue; }
            if (_src[_pos] == '[') return;  // 다음 섹션 시작
            ParseKeyValue(table);
        }
    }

    private (string name, bool isArray) ParseSectionHeader()
    {
        // [name] or [[name]]
        _pos++; // '['
        bool isArray = false;
        if (_pos < _src.Length && _src[_pos] == '[') { isArray = true; _pos++; }
        SkipWhitespace();
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != ']' && _src[_pos] != '\n')
        {
            sb.Append(_src[_pos]);
            _pos++;
        }
        // 닫는 ]
        if (_pos < _src.Length && _src[_pos] == ']') _pos++;
        if (isArray && _pos < _src.Length && _src[_pos] == ']') _pos++;
        SkipWhitespace();
        if (_pos < _src.Length && _src[_pos] == '\n') _pos++;
        return (sb.ToString().Trim(), isArray);
    }

    private bool ParseKeyValue(TomlTable table)
    {
        SkipWhitespace();
        // key: bare 또는 "quoted"
        var key = ParseKey();
        if (string.IsNullOrEmpty(key)) { SkipLine(); return false; }
        SkipWhitespace();
        if (_pos >= _src.Length || _src[_pos] != '=') { SkipLine(); return false; }
        _pos++; // '='
        SkipWhitespace();
        var val = ParseValue();
        // 줄 끝까지 주석/공백 소비
        SkipToEndOfLine();
        if (val != null) table.Set(key, val);
        return true;
    }

    private string ParseKey()
    {
        if (_pos >= _src.Length) return "";
        if (_src[_pos] == '"')
        {
            // "key" (이스케이프 지원)
            _pos++;
            var sb = new StringBuilder();
            while (_pos < _src.Length && _src[_pos] != '"')
            {
                if (_src[_pos] == '\\' && _pos + 1 < _src.Length) { sb.Append(_src[_pos + 1]); _pos += 2; }
                else { sb.Append(_src[_pos]); _pos++; }
            }
            if (_pos < _src.Length) _pos++; // closing "
            return sb.ToString();
        }
        // bare key: [A-Za-z0-9_-]+
        var sb2 = new StringBuilder();
        while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '-'))
        {
            sb2.Append(_src[_pos]);
            _pos++;
        }
        return sb2.ToString();
    }

    private object? ParseValue()
    {
        if (_pos >= _src.Length) return null;
        var c = _src[_pos];
        if (c == '"') return ParseString();
        if (c == '\'') return ParseLiteralString();
        if (c == '[') return ParseArray();
        if (c == '{') return ParseInlineTable();
        if (c == 't' || c == 'f') return ParseBool();
        // number (또는 unrecognized → string fallback)
        if (c == '-' || c == '+' || char.IsDigit(c)) return ParseNumberOrDate();
        // 알 수 없는 경우 — 줄 끝까지 그대로 읽어 string 으로
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '#')
        { sb.Append(_src[_pos]); _pos++; }
        return sb.ToString().Trim();
    }

    private string ParseString()
    {
        _pos++; // opening "
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '"')
        {
            if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
            {
                var n = _src[_pos + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(n); break;
                }
                _pos += 2;
            }
            else { sb.Append(_src[_pos]); _pos++; }
        }
        if (_pos < _src.Length) _pos++; // closing "
        return sb.ToString();
    }

    private string ParseLiteralString()
    {
        _pos++; // opening '
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '\'')
        { sb.Append(_src[_pos]); _pos++; }
        if (_pos < _src.Length) _pos++;
        return sb.ToString();
    }

    private List<object?> ParseArray()
    {
        _pos++; // '['
        var list = new List<object?>();
        while (_pos < _src.Length)
        {
            SkipWhitespaceAndNewlines();
            if (_pos < _src.Length && _src[_pos] == ']') { _pos++; break; }
            if (_pos < _src.Length && _src[_pos] == ',') { _pos++; continue; }
            var v = ParseValue();
            if (v != null) list.Add(v);
        }
        return list;
    }

    private TomlTable ParseInlineTable()
    {
        _pos++; // '{'
        var t = new TomlTable("");
        while (_pos < _src.Length)
        {
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == '}') { _pos++; break; }
            if (_pos < _src.Length && _src[_pos] == ',') { _pos++; continue; }
            var k = ParseKey();
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == '=') { _pos++; SkipWhitespace(); }
            var v = ParseValue();
            if (v != null) t.Set(k, v);
        }
        return t;
    }

    private bool ParseBool()
    {
        if (_pos + 4 <= _src.Length && _src.Substring(_pos, 4) == "true") { _pos += 4; return true; }
        if (_pos + 5 <= _src.Length && _src.Substring(_pos, 5) == "false") { _pos += 5; return false; }
        return false;
    }

    private object? ParseNumberOrDate()
    {
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != ',')
        {
            // # 만나면 멈춤 (주석)
            if (_src[_pos] == '#') break;
            sb.Append(_src[_pos]);
            _pos++;
        }
        var s = sb.ToString().Trim();
        // 정수/실수 시도
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return s; // string fallback (datetime 등)
    }

    private void SkipWhitespace()
    {
        while (_pos < _src.Length && (_src[_pos] == ' ' || _src[_pos] == '\t')) _pos++;
    }

    private void SkipWhitespaceAndNewlines()
    {
        while (_pos < _src.Length && (_src[_pos] == ' ' || _src[_pos] == '\t' || _src[_pos] == '\n' || _src[_pos] == '\r')) _pos++;
    }

    private void SkipLine()
    {
        while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
        if (_pos < _src.Length) _pos++;
    }

    private void SkipToEndOfLine()
    {
        while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '#') _pos++;
        if (_pos < _src.Length && _src[_pos] == '#') SkipLine();
    }

    private static TomlTable EnsureTable(TomlTable root, string dotted)
    {
        var parts = dotted.Split('.');
        var cur = root;
        for (int i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Trim().Trim('"');
            if (!cur.TryGetTable(name, out var next))
            {
                next = new TomlTable(name);
                cur.Set(name, next);
            }
            cur = next;
        }
        return cur;
    }
}

/// <summary>TomlTable: 키→객체. dict-like. <see cref="IEnumerable{T}"/> 는 (key,value) 튜플.</summary>
public sealed class TomlTable : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly string _name;
    private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

    public TomlTable(string name) { _name = name; }

    public string Name => _name;
    public TomlValueKind ValueKind => TomlValueKind.Table;

    public void Set(string key, object? value) => _data[key] = value;
    public bool TryGetValue(string key, out object? value) => _data.TryGetValue(key, out value);

    public bool TryGetString(string key, out string value)
    {
        if (_data.TryGetValue(key, out var v) && v is string s) { value = s; return true; }
        value = "";
        return false;
    }
    public bool TryGetArray(string key, out List<object?> list)
    {
        if (_data.TryGetValue(key, out var v) && v is List<object?> l) { list = l; return true; }
        list = new List<object?>();
        return false;
    }
    public bool TryGetTable(string key, out TomlTable t)
    {
        if (_data.TryGetValue(key, out var v) && v is TomlTable tt) { t = tt; return true; }
        t = new TomlTable(key);
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _data.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _data.GetEnumerator();
    public int Count => _data.Count;
    public IEnumerable<string> Keys => _data.Keys;
}

public enum TomlValueKind { Table, Array, Scalar }
