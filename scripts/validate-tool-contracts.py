#!/usr/bin/env python3
"""Validate the published MCP tool schemas and their executable examples.

This is intentionally dependency-free so the same guard can run in a clean CI
runner. It checks the JSON Schema subset used by the Gateway (objects, arrays,
primitive types, required fields, enums and additionalProperties) and fails
closed when an example is not a valid invocation for its tool.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "src" / "GxMcp.Gateway" / "tool_definitions.json"
ROUTER_FILES = ("AnalyzeRouter.cs", "SearchRouter.cs", "ObjectRouter.cs")
CAPABILITIES_INVENTORY = ROOT / "docs" / "mcp_capabilities_inventory.md"


# Existing top-level description debt is an explicit baseline, not permission
# for new debt. Nested item schemas are intentionally outside this incremental
# gate; the baseline below is the 77 top-level properties measured for #186,
# minus the two action properties fixed by this issue.
KNOWN_MISSING_PROPERTY_DESCRIPTIONS = frozenset(
    {
        ("genexus_data_view", "attributeMappings"),
        ("genexus_data_view", "updatable"),
        ("genexus_query", "query"),
        ("genexus_query", "typeFilter"),
        ("genexus_query", "domainFilter"),
        ("genexus_query", "limit"),
        ("genexus_list_objects", "limit"),
        ("genexus_list_objects", "offset"),
        ("genexus_list_objects", "parent"),
        ("genexus_list_objects", "parentPath"),
        ("genexus_list_objects", "typeFilter"),
        ("genexus_read", "name"),
        ("genexus_read", "targets"),
        ("genexus_read", "offset"),
        ("genexus_read", "limit"),
        ("genexus_read", "type"),
        ("genexus_edit", "name"),
        ("genexus_edit", "part"),
        ("genexus_edit", "mode"),
        ("genexus_edit", "content"),
        ("genexus_edit", "operation"),
        ("genexus_edit", "expectedCount"),
        ("genexus_edit", "dryRun"),
        ("genexus_edit", "verifyRollback"),
        ("genexus_edit", "type"),
        ("genexus_edit", "changeSet"),
        ("genexus_inspect", "name"),
        ("genexus_inspect", "include"),
        ("genexus_inspect", "type"),
        ("genexus_analyze", "name"),
        ("genexus_analyze", "mode"),
        ("genexus_test", "name"),
        ("genexus_worker_reload", "mode"),
        ("genexus_delete_object", "name"),
        ("genexus_delete_object", "type"),
        ("genexus_delete_object", "confirm"),
        ("genexus_run_object", "name"),
        ("genexus_format", "code"),
        ("genexus_properties", "name"),
        ("genexus_properties", "value"),
        ("genexus_search_source", "pattern"),
        ("genexus_search_source", "typeFilter"),
        ("genexus_search_source", "maxResults"),
        ("genexus_search_source", "caseSensitive"),
        ("genexus_search_source", "includeComments"),
        ("genexus_navigation", "name"),
        ("genexus_api", "routes"),
        ("genexus_edit_and_build", "mode"),
        ("genexus_edit_and_build", "dryRun"),
        ("genexus_edit_and_build", "validate"),
        ("genexus_edit_and_build", "validationMode"),
        ("genexus_edit_and_build", "rollbackOnFailure"),
        ("genexus_edit_and_build", "buildIncludeCallees"),
        ("genexus_edit_and_build", "buildPlanCap"),
        ("genexus_edit_and_build", "waitForIndex"),
        ("genexus_edit_and_build", "waitTimeoutMs"),
        ("genexus_compare", "objectA"),
        ("genexus_compare", "objectB"),
        ("genexus_merge", "objectLeft"),
        ("genexus_merge", "objectRight"),
        ("genexus_variable", "name"),
        ("genexus_wwp", "entityKey"),
        ("genexus_wwp", "value"),
        ("genexus_wwp", "offset"),
        ("genexus_kb_diff", "kbA"),
        ("genexus_kb_diff", "kbB"),
        ("genexus_kb_import", "from"),
        ("genexus_kb_import", "name"),
        ("genexus_kb_import", "type"),
        ("genexus_kb_import", "to"),
        ("genexus_sandbox", "name"),
        ("genexus_sandbox", "from"),
        ("genexus_sandbox", "overwrite"),
        ("genexus_worker_pool", "spareCount"),
        ("genexus_worker_pool", "count"),
    }
)


# These are compatibility or infrastructure reads that are intentionally not
# published as first-class schema properties. Every exception is keyed by the
# concrete router and tool so a new undeclared access cannot hide behind a
# broad global alias list.
ALLOWED_UNDECLARED_ROUTER_PARAMETERS = frozenset(
    {
        ("AnalyzeRouter.cs", "genexus_inspect", "target"),
        ("AnalyzeRouter.cs", "genexus_analyze", "target"),
        ("AnalyzeRouter.cs", "genexus_analyze", "path"),
        ("AnalyzeRouter.cs", "genexus_analyze", "entityKey"),
        ("AnalyzeRouter.cs", "genexus_analyze", "guid"),
        ("AnalyzeRouter.cs", "genexus_analyze", "type"),
        ("AnalyzeRouter.cs", "genexus_analyze", "cancelToken"),
        ("SearchRouter.cs", "genexus_query", "filter"),
        ("SearchRouter.cs", "genexus_query", "type"),
        ("SearchRouter.cs", "genexus_search_source", "type"),
        ("SearchRouter.cs", "genexus_list_objects", "type"),
        ("ObjectRouter.cs", "genexus_edit", "path"),
        ("ObjectRouter.cs", "genexus_edit", "entityKey"),
        ("ObjectRouter.cs", "genexus_edit", "guid"),
        ("ObjectRouter.cs", "genexus_edit", "autoInjectVariables"),
        ("ObjectRouter.cs", "genexus_edit", "changes"),
        # Issue #357 internal transport metadata (same class as `_gxmcpOwnerId`):
        # the Gateway sets it on the routed command when a post-write recovery
        # fence forces an authoritative read. Deliberately NOT a published schema
        # property — a caller must not be able to request it.
        ("ObjectRouter.cs", "genexus_read", "_requireAuthoritativeRead"),
        # Tolerated misspelling: genexus_search_source/genexus_refactor call the
        # object `objectName`, and agents carry it over. `name` stays the published
        # field; the alias is read only when `name` is absent.
        ("ObjectRouter.cs", "genexus_read", "objectName"),
        ("ObjectRouter.cs", "genexus_edit_and_build", "path"),
        ("ObjectRouter.cs", "genexus_edit_and_build", "entityKey"),
        ("ObjectRouter.cs", "genexus_edit_and_build", "guid"),
    }
)


class ContractError(ValueError):
    pass


def _type_matches(value: Any, expected: str) -> bool:
    return {
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
        "boolean": isinstance(value, bool),
        "null": value is None,
    }.get(expected, True)


def validate_value(value: Any, schema: dict[str, Any], path: str) -> None:
    if not isinstance(schema, dict):
        raise ContractError(f"{path}: schema must be an object")

    expected = schema.get("type")
    if isinstance(expected, list):
        if not any(_type_matches(value, item) for item in expected):
            raise ContractError(f"{path}: expected one of {expected}, got {type(value).__name__}")
    elif isinstance(expected, str) and not _type_matches(value, expected):
        raise ContractError(f"{path}: expected {expected}, got {type(value).__name__}")

    enum = schema.get("enum")
    if isinstance(enum, list) and value not in enum:
        raise ContractError(f"{path}: value {value!r} is not in enum")

    if isinstance(value, dict):
        required = schema.get("required", [])
        if not isinstance(required, list):
            raise ContractError(f"{path}: required must be an array")
        for name in required:
            if name not in value:
                raise ContractError(f"{path}: missing required property {name!r}")
        properties = schema.get("properties", {})
        if not isinstance(properties, dict):
            raise ContractError(f"{path}: properties must be an object")
        if schema.get("additionalProperties") is False:
            unknown = sorted(set(value) - set(properties))
            if unknown:
                raise ContractError(f"{path}: unknown properties {unknown}")
        for name, child in value.items():
            if name in properties:
                validate_value(child, properties[name], f"{path}.{name}")

    if isinstance(value, list) and isinstance(schema.get("items"), dict):
        for index, item in enumerate(value):
            validate_value(item, schema["items"], f"{path}[{index}]")


def _has_description(value: Any) -> bool:
    return isinstance(value, dict) and isinstance(value.get("description"), str) and bool(
        value["description"].strip()
    )


def _missing_top_level_descriptions(document: list[Any]) -> set[tuple[str, str]]:
    missing: set[tuple[str, str]] = set()
    for tool in document:
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str):
            continue
        schema = tool.get("inputSchema")
        properties = schema.get("properties", {}) if isinstance(schema, dict) else {}
        if not isinstance(properties, dict):
            continue
        for property_name, property_schema in properties.items():
            if not _has_description(property_schema):
                missing.add((tool["name"], property_name))
    return missing


def validate_descriptions(document: list[Any]) -> dict[str, int]:
    """Freeze the known top-level debt while rejecting any new debt.

    Nested item/$defs properties are intentionally not part of this incremental
    gate. The baseline is the measured top-level debt from issue #186.
    """
    missing = _missing_top_level_descriptions(document)
    action_debt = sorted(
        (tool, property_name)
        for tool, property_name in missing
        if property_name == "action"
    )
    new_debt = sorted(missing - KNOWN_MISSING_PROPERTY_DESCRIPTIONS)
    if action_debt:
        formatted = ", ".join(f"{tool}.{property_name}" for tool, property_name in action_debt)
        raise ContractError("action properties must have descriptions: " + formatted)
    if new_debt:
        formatted = ", ".join(f"{tool}.{property_name}" for tool, property_name in new_debt)
        raise ContractError("new top-level properties missing description: " + formatted)
    return {
        "known_debt": len(missing & KNOWN_MISSING_PROPERTY_DESCRIPTIONS),
        "action_debt": len(action_debt),
        "new_debt": len(new_debt),
    }


def validate_capabilities_inventory(
    document: list[Any], path: Path = CAPABILITIES_INVENTORY
) -> dict[str, int]:
    """Keep the human-readable action table aligned with the published schema."""
    schema_actions: dict[str, set[str]] = {}
    for tool in document:
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str):
            continue
        properties = tool.get("inputSchema", {}).get("properties", {})
        action = properties.get("action") if isinstance(properties, dict) else None
        actions = action.get("enum") if isinstance(action, dict) else None
        if isinstance(actions, list):
            schema_actions[tool["name"]] = {str(value) for value in actions}

    rows: dict[str, tuple[set[str], set[str]]] = {}
    in_action_table = False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            in_action_table = line.casefold().startswith("## action contract")
            continue
        if not in_action_table or not line.startswith("|"):
            continue
        cells = [cell.strip() for cell in line.split("|")]
        if len(cells) < 4 or cells[1].casefold() == "tool":
            continue
        match = re.fullmatch(r"`([^`]+)`", cells[1])
        if not match:
            continue
        tool = match.group(1)
        if tool in rows:
            raise ContractError(f"duplicate capabilities inventory row for {tool}")
        rows[tool] = (
            set(re.findall(r"`([^`]+)`", cells[2])),
            set(re.findall(r"`([^`]+)`", cells[3])),
        )

    missing_tools = sorted(set(schema_actions) - set(rows))
    extra_tools = sorted(set(rows) - set(schema_actions))
    if missing_tools or extra_tools:
        details = []
        if missing_tools:
            details.append("missing=" + ", ".join(missing_tools))
        if extra_tools:
            details.append("extra=" + ", ".join(extra_tools))
        raise ContractError("capabilities inventory tools differ from schema: " + "; ".join(details))

    mismatches = []
    for tool, actions in schema_actions.items():
        documented = rows[tool][0] | rows[tool][1]
        if documented != actions:
            mismatches.append(
                f"{tool}: schema={','.join(sorted(actions))}; "
                f"inventory={','.join(sorted(documented))}"
            )
    if mismatches:
        raise ContractError("capabilities inventory actions differ from schema: " + "; ".join(mismatches))

    return {
        "tools": len(rows),
        "actions": sum(len(read_only | mutating) for read_only, mutating in rows.values()),
    }


def _strip_csharp_comments(source: str) -> str:
    source = re.sub(r"/\*.*?\*/", "", source, flags=re.DOTALL)
    return re.sub(r"//.*$", "", source, flags=re.MULTILINE)


def _argument_keys(source: str) -> set[str]:
    return set(re.findall(r'\bargs\s*\??\s*\[\s*["\']([^"\']+)["\']\s*\]', source))


def _router_case_groups(source: str) -> list[tuple[list[str], str, set[str]]]:
    """Return outer genexus_* case groups and their direct args[] reads."""
    source = _strip_csharp_comments(source)
    method_start = source.find("ConvertToolCall")
    if method_start < 0:
        raise ContractError("router source has no ConvertToolCall method")
    lines = source[method_start:].splitlines()
    candidates: list[tuple[int, int, str]] = []
    for index, line in enumerate(lines):
        match = re.match(r'^(\s*)case\s+"(genexus_[^"]+)"\s*:', line)
        if match:
            candidates.append((index, len(match.group(1)), match.group(2)))
    if not candidates:
        raise ContractError("router source has no genexus_* cases")

    outer_indent = min(indent for _, indent, _ in candidates)
    outer = [item for item in candidates if item[1] == outer_indent]
    groups: list[tuple[list[str], str, set[str]]] = []
    position = 0
    while position < len(outer):
        start, _, first_tool = outer[position]
        labels = [first_tool]
        next_position = position + 1
        while next_position < len(outer) and outer[next_position][0] == outer[next_position - 1][0] + 1:
            labels.append(outer[next_position][2])
            next_position += 1
        end = outer[next_position][0] if next_position < len(outer) else len(lines)
        block = "\n".join(lines[start:end])
        groups.append((labels, block, _argument_keys(block)))
        position = next_position
    return groups


def validate_router_parameters(
    document: list[Any], source_root: Path = ROOT
) -> dict[str, int]:
    """Check direct router args[] reads against published schemas.

    Only the Analyze, Search, and Object routers are covered here. Unpublished
    legacy cases are ignored; published compatibility/infrastructure reads must
    be represented by the concrete allowlist above.
    """
    schemas = {}
    for tool in document:
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str):
            continue
        schema = tool.get("inputSchema")
        properties = schema.get("properties", {}) if isinstance(schema, dict) else {}
        schemas[tool["name"]] = properties

    checked_tools: set[str] = set()
    consumed = 0
    declared = 0
    allowlisted = 0
    missing: list[str] = []
    router_dir = source_root / "src" / "GxMcp.Gateway" / "Routers"

    for router_name in ROUTER_FILES:
        path = router_dir / router_name
        if not path.is_file():
            raise ContractError(f"router source is missing: {path}")
        source = path.read_text(encoding="utf-8")
        groups = _router_case_groups(source)
        common_start = _strip_csharp_comments(source).find("ConvertToolCall")
        # The common target/type normalization before the switch applies to
        # every published case in that router.
        normalized_source = _strip_csharp_comments(source)[common_start:]
        first_case = min(
            match.start()
            for match in re.finditer(r'^\s*case\s+"genexus_[^"]+"\s*:', normalized_source, re.MULTILINE)
        )
        common_keys = _argument_keys(normalized_source[:first_case])

        for labels, _, case_keys in groups:
            published = [tool for tool in labels if tool in schemas]
            if not published:
                continue
            for tool in published:
                checked_tools.add(tool)
                properties = schemas[tool] if isinstance(schemas[tool], dict) else {}
                for parameter in sorted(common_keys | case_keys):
                    consumed += 1
                    if parameter in properties:
                        declared += 1
                        continue
                    key = (router_name, tool, parameter)
                    if key in ALLOWED_UNDECLARED_ROUTER_PARAMETERS:
                        allowlisted += 1
                        continue
                    missing.append(f"{router_name}:{tool}.{parameter}")

    if missing:
        raise ContractError(
            "undeclared router parameters (add the schema field or a keyed "
            "compatibility allowlist entry): " + ", ".join(sorted(missing))
        )
    return {
        "tools": len(checked_tools),
        "consumed": consumed,
        "declared": declared,
        "allowlisted": allowlisted,
    }


def validate_line_layout(path: Path, tool_count: int) -> dict[str, int]:
    """Keep tool_definitions.json as one complete tool object per line.

    Surgical schema edits replace a single line; a whole-file reformat buries
    the real change in diff noise and can desync the discovery golden fixture.
    Every non-bracket line must parse as exactly one tool object.
    """
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines or lines[0].strip() != "[" or lines[-1].strip() != "]":
        raise ContractError("tool definitions must be a [<newline>...<newline>] document")
    body = [line for line in lines[1:-1] if line.strip()]
    if len(body) != tool_count:
        raise ContractError(
            f"tool definitions must carry one tool per line: "
            f"{len(body)} lines for {tool_count} tools"
        )
    for index, line in enumerate(body):
        text = line.strip()
        if index < len(body) - 1:
            if not text.endswith(","):
                raise ContractError(f"tools line {index + 2}: expected trailing comma")
            text = text[:-1]
        try:
            tool = json.loads(text)
        except json.JSONDecodeError as error:
            raise ContractError(f"tools line {index + 2}: not one complete tool object ({error})")
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str):
            raise ContractError(f"tools line {index + 2}: expected a tool object with a name")
    return {"lines": len(body)}


def validate_tool(tool: dict[str, Any], index: int) -> int:
    name = tool.get("name")
    if not isinstance(name, str) or not name.strip():
        raise ContractError(f"tools[{index}]: name is required")
    schema = tool.get("inputSchema")
    if not isinstance(schema, dict) or schema.get("type") != "object":
        raise ContractError(f"{name}: inputSchema must be an object schema")
    properties = schema.get("properties", {})
    if not isinstance(properties, dict):
        raise ContractError(f"{name}: inputSchema.properties must be an object")

    action = properties.get("action")
    actions = action.get("enum", []) if isinstance(action, dict) else []
    if len(actions) != len(set(actions)):
        raise ContractError(f"{name}: action enum contains duplicates")
    examples = schema.get("examples", [])
    if not isinstance(examples, list) or not examples:
        raise ContractError(f"{name}: inputSchema.examples must contain at least one example")
    for example_index, example in enumerate(examples):
        validate_value(example, schema, f"{name}.examples[{example_index}]")
    return len(actions)


def validate_document(document: Any, source_root: Path = ROOT) -> dict[str, int]:
    if not isinstance(document, list) or not document:
        raise ContractError("tool definitions must be a non-empty array")
    names: set[str] = set()
    action_count = 0
    for index, tool in enumerate(document):
        if not isinstance(tool, dict):
            raise ContractError(f"tools[{index}]: expected object")
        name = tool.get("name")
        if name in names:
            raise ContractError(f"duplicate tool name {name!r}")
        names.add(name)
        action_count += validate_tool(tool, index)
    description_counts = validate_descriptions(document)
    router_counts = validate_router_parameters(document, source_root)
    return {
        "tools": len(document),
        "actions": action_count,
        "knownDescriptionDebt": description_counts["known_debt"],
        "actionDescriptionDebt": description_counts["action_debt"],
        "routerTools": router_counts["tools"],
        "routerParameters": router_counts["consumed"],
        "allowlistedRouterParameters": router_counts["allowlisted"],
    }


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", type=Path, default=TOOLS)
    args = parser.parse_args(list(argv) if argv is not None else None)
    try:
        document = json.loads(args.path.read_text(encoding="utf-8"))
        counts = validate_document(document)
        validate_line_layout(args.path, counts["tools"])
        capabilities = validate_capabilities_inventory(document)
        print(
            "tool-contracts: valid "
            f"tools={counts['tools']} actions={counts['actions']} "
            f"knownDescriptionDebt={counts['knownDescriptionDebt']} "
            f"actionDescriptionDebt={counts['actionDescriptionDebt']} "
            f"routerTools={counts['routerTools']} "
            f"routerParameters={counts['routerParameters']} "
            f"allowlistedRouterParameters={counts['allowlistedRouterParameters']} "
            f"capabilitiesTools={capabilities['tools']} "
            f"capabilitiesActions={capabilities['actions']}"
        )
        return 0
    except (OSError, json.JSONDecodeError, ContractError) as error:
        print(f"tool-contracts: invalid: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
