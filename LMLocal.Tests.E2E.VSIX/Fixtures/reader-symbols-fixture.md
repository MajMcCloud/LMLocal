# Reader Symbol Fixture — deterministic special-character scenarios

Ground truth for the code_reader_subagent E2E regression. Focus: tokens the
model often mangles — spaces, quotes, brackets, and the U+2581 marker (▁).
Each line is a unique anchor so fixture retrieval stays unambiguous.

NOTE about U+2581 (LOWER ONE EIGHTH BLOCK, ▁): many editors and middleware
layers normalize this byte into an ordinary space. Because the code_reader
subagent retrieves text through read_file_lines, which may also normalize the
byte, this fixture intentionally covers the characters that drive the
parser's nesting-depth logic — spaces, quotes, and brackets. The raw U+2581
marker is covered separately by a byte-level round-trip check outside this
fixture.

## Section: spaces

SYM-SPACE-MID: two  spaces  between  words
SYM-TAB-SEPARATED: tab	value

## Section: single quotes

SYM-QUOTE-SINGLE: it's an 'inline' quote
SYM-QUOTE-NESTED-SINGLE: 'outer "inner" value'

## Section: double quotes

SYM-QUOTE-DOUBLE: "double quoted value"
SYM-QUOTE-NESTED-DOUBLE: "outer 'inner' value"
SYM-QUOTE-AT-START: "leading quote
SYM-QUOTE-AT-END: trailing quote"
SYM-QUOTE-BACKTICK: `inline code` and ``double backtick``
SYM-QUOTE-MIXED-ALL: 'single' "double" `backtick`
SYM-QUOTE-LONE-DOUBLE: a single " in the middle
SYM-QUOTE-LONE-SINGLE: a single ' in the middle
SYM-QUOTE-EMPTY-PAIR: "" and ''

## Section: brackets

SYM-PAREN-OPEN: function(
SYM-PAREN-CLOSE: )
SYM-PAREN-BALANCED: ((a, b), [c, d], {e: f})
SYM-BRACKET-SQUARE: list[0] and array["key"]
SYM-BRACKET-CURLY: {"key": "value"}
SYM-BRACKET-MIXED: {[("a", {"b": [1,2]})]}
SYM-BRACKET-MIXED2: [{"a":"a"},{"a:"}]
SYM-BRACKET-MIXED3: [{"a:a"},{"a: "}]

## Section: braces inside strings

SYM-BRACE-CLOSE-IN-STRING: {"a":"}"}
SYM-BRACE-OPEN-IN-STRING: {"a":"{"}
SYM-BRACE-TRIPLE-CLOSE: }}}
SYM-BRACE-TRIPLE-OPEN: {{{
SYM-BRACE-BRACKET-IN-STRING: {"a":"]["}
SYM-BRACE-UNBALANCED-IN-STRING: ["(", "{", "["]

## Section: empty containers

SYM-EMPTY-OBJECT: {}
SYM-EMPTY-ARRAY: []
SYM-EMPTY-STRING-VALUE: {"a":""}
SYM-EMPTY-ARRAY-OF-OBJECTS: [{}]
SYM-EMPTY-NESTED-ARRAY: [[]]
SYM-EMPTY-NESTED-MIX: {"a":{},"b":[],"c":""}

## Section: escapes

SYM-ESCAPE-QUOTE: a\"b
SYM-ESCAPE-BACKSLASH: a\\b
SYM-ESCAPE-NEWLINE-LITERAL: line1\nline2
SYM-ESCAPE-TAB-LITERAL: col1\tcol2
SYM-ESCAPE-WINDOWS-PATH: C:\dir\file.txt
SYM-ESCAPE-JSON-IN-STRING: {"a":"{\"b\":\"c\"}"}
SYM-ESCAPE-TRAILING-BACKSLASH: ends with a backslash\

## Section: colon and comma inside values

SYM-COLON-COMMA-IN-STRING: "a:b,c:d"
SYM-KEYVALUE-LOOKALIKE: key: value, key2: value2
SYM-COLON-SPACE-IN-STRING: {"note":"time is 12:30, ok"}
SYM-COMMA-ONLY: ,,,
SYM-COLON-ONLY: :::

## Section: literal lookalikes

SYM-LITERAL-TRUE: {"a":"true"}
SYM-LITERAL-FALSE: {"a":"false"}
SYM-LITERAL-NULL: {"a":"null"}
SYM-LITERAL-NUMBER-STRING: {"a":"123"}
SYM-LITERAL-FLOAT-STRING: {"a":"1e5"}
SYM-LITERAL-BARE: {a:true,b:null,c:42}

## Section: call syntax as plain text

SYM-CALL-LOOKALIKE: call:foo{bar}
SYM-CALL-LOOKALIKE-QUOTED: call:foo{"bar":"baz"}
SYM-CALL-LOOKALIKE-TAG: <call>foo{bar}

## Section: nesting depth

SYM-NEST-DEEP-OPEN: {[{[{[
SYM-NEST-DEEP-CLOSE: ]}]}]}
SYM-NEST-DEEP-BALANCED: {"a":[{"b":[{"c":[{"d":[1]}]}]}]}
SYM-NEST-STRINGS-IN-ARRAYS: [["a","b"],["c",["d","e"]]]

## Section: unicode scripts

SYM-UNICODE-CYRILLIC: привет, мир
SYM-UNICODE-CHINESE: 你好，世界
SYM-UNICODE-CHINESE-MIXED: 文件 file.txt 在 C:\目录\文件.txt 中
SYM-UNICODE-ESTONIAN: Tere, maailm! Öö, äär, õun, üle, šokolaad, žürii
SYM-UNICODE-JAPANESE: こんにちは、世界
SYM-UNICODE-ARABIC-RTL: مرحبا بالعالم
SYM-UNICODE-COMBINING: e + U+0301 = é, and precomposed é
SYM-UNICODE-CJK-BRACKETS: （你好）【世界】「引号」『二重』
SYM-UNICODE-CJK-QUOTES: “双引号” and ‘单引号’
SYM-UNICODE-FULLWIDTH-JSON: ｛＂ａ＂：＂ｂ＂｝
SYM-UNICODE-JSON-CJK: {"键":"值","列表":["一","二"]}
SYM-UNICODE-JSON-ESTONIAN: {"nimi":"Jüri Õun","linn":"Tartu"}


## Section: other characters

SYM-UNICODE-EMOJI: ok 👍 done ✅
SYM-ANGLE-AMP: <div class="x">a &amp; b</div>
SYM-SHELL-LIKE: echo "$HOME" && ls | grep 'x'
SYM-REGEX-LIKE: ^\d{3}-[a-z]+(foo|bar)?$
SYM-FENCE-MARKER: ```
SYM-FENCE-LANG: ```json
SYM-LONG-LINE-END-BRACE: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}

## Section: empty-line handling

SYM-EMPTY-NEXT

SYM-EMPTY-AFTER: this line follows the empty line above

## Tail

SYM-END-TAIL: last line anchor