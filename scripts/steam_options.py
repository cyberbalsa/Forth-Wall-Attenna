"""Read/patch one Steam app's launch options while retaining all other bytes."""
from dataclasses import dataclass, field
import re


@dataclass
class Node:
    key: str
    start: int
    end: int
    value: str | None = None
    value_start: int = 0
    value_end: int = 0
    children: list = field(default_factory=list)


def unquote(text):
    return re.sub(r'\\(["\\])', r'\1', text[1:-1])


def quote(text):
    return '"' + text.replace('\\', '\\\\').replace('"', '\\"') + '"'


def parse(text):
    tokens = list(re.finditer(r'//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]', text))
    tokens = [t for t in tokens if not t.group().startswith('//')]
    cursor = 0

    def nodes(nested=False):
        nonlocal cursor
        result = []
        while cursor < len(tokens):
            key = tokens[cursor]
            if key.group() == '}':
                if not nested:
                    raise ValueError('Unexpected VDF closing brace')
                cursor += 1
                return result, key.start()
            if not key.group().startswith('"') or cursor + 1 >= len(tokens):
                raise ValueError('Invalid VDF entry')
            value = tokens[cursor + 1]
            cursor += 2
            if value.group() == '{':
                children, end = nodes(True)
                result.append(Node(unquote(key.group()), key.start(), end, children=children))
            elif value.group().startswith('"'):
                result.append(Node(unquote(key.group()), key.start(), value.end(), unquote(value.group()), value.start(), value.end()))
            else:
                raise ValueError('Invalid VDF value')
        if nested:
            raise ValueError('Unclosed VDF object')
        return result, len(text)

    return nodes()[0]


def app_node(text, app_id='244850'):
    nodes = parse(text)
    for key in ('UserLocalConfigStore', 'Software', 'Valve', 'Steam', 'apps', app_id):
        matches = [node for node in nodes if node.key.casefold() == key.casefold()]
        if len(matches) != 1:
            raise ValueError(f'Expected exactly one Steam config object for {key}')
        selected = matches[0]
        nodes = selected.children
    return selected


def get_options(text):
    values = [n.value for n in app_node(text).children if n.key.casefold() == 'launchoptions']
    if len(values) > 1:
        raise ValueError('Duplicate launch options')
    return values[0] if values else None


def set_options(text, value):
    app = app_node(text)
    matches = [n for n in app.children if n.key.casefold() == 'launchoptions']
    if len(matches) > 1:
        raise ValueError('Duplicate launch options')
    if matches:
        node = matches[0]
        return text[:node.value_start] + quote(value) + text[node.value_end:]
    line_start = text.rfind('\n', 0, app.end) + 1
    indent = text[line_start:app.end]
    if indent.strip():
        return text[:app.end] + '\n"LaunchOptions" ' + quote(value) + '\n' + text[app.end:]
    newline = '\r\n' if '\r\n' in text else '\n'
    return text[:line_start] + indent + '\t"LaunchOptions"\t\t' + quote(value) + newline + text[line_start:]
