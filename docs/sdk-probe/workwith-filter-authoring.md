# WorkWith (standard) filter authoring — SDK feasibility, GeneXus 18

Answers issue [#351](https://github.com/lennix1337/Genexus18MCP/issues/351): is there a
known SDK route to add a `filterAttribute` and its conditions to an already-applied
**standard** WorkWith (GX18), as opposed to a WorkWithPlus pattern?

**Answer: yes, and it is typed.** Every step of the route is a public member of the
installed `Artech.Patterns.WorkWith` assembly. This is a surface reading of the
installed SDK, not an executed authoring run — see *What is not yet proven*.

## Where the evidence comes from

`genexus_sdk_probe mode=surface` against GeneXus 18.0.10.184260, plus the shipped
package manifest. Two artifacts:

- `Packages/Patterns/WorkWith/Artech.Patterns.WorkWith.dll`
- `Packages/Patterns/WorkWith/WorkWithInstance.xml` — the instance schema

Note the path. A previous check looked for `GeneXus\Packages\Patterns`, which does not
exist; the pattern packages live under the **major** directory,
`GeneXus\GeneXus18\Packages\Patterns`. Standard WorkWith is a built-in pattern and *is*
installed. `WorkWithPlus` is a DVelop package and is *not* present on this machine —
which is why [#349](https://github.com/lennix1337/Genexus18MCP/issues/349) and
[#359](https://github.com/lennix1337/Genexus18MCP/issues/359) cannot be reproduced
here while this one can.

## The route

```csharp
// 1. Enter and leave the typed model. PatternInstance is what the SDK persists.
var workWith = Artech.Patterns.WorkWith.WorkWithInstance.Load(patternInstance);
try {
    // 2. Resolve the selection explicitly — an instance has many Levels.
    var level = workWith.FindLevel(levelId);          // or workWith.Levels
    var filter = level.Selection.Filter;

    // 3. Refuse a duplicate before mutating (the reporter's own criterion).
    if (filter.FindFilterAttribute("TemExistente") != null) { /* reject */ }

    // 4. Author the filter attribute.
    var attr = filter.AddFilterAttribute("TemExistente");
    attr.Description = "Tem Existente";
    attr.Domain      = simOuNaoDomain;                 // optional; only for variables

    // 5. Author the conditions — a separate node, exactly as suspected.
    filter.AddCondition("DocumentoTesteRomaneioId.IsEmpty() or ... when &TemExistente = SimOuNao.Nao;");
    filter.AddCondition("not DocumentoTesteRomaneioId.IsEmpty() and ... when &TemExistente = SimOuNao.Sim;");

    workWith.SaveTo(patternInstance);
}
```

## The members, verbatim

`Artech.Patterns.WorkWith.WorkWithInstance`

| Member | Signature |
|---|---|
| enter | `static WorkWithInstance Load(PatternInstance instance)` |
| enter (cheap) | `static WorkWithInstance FastLoad(PatternInstance instance)` |
| leave | `void SaveTo(PatternInstance instance)` |
| resolve level | `LevelElement AddLevel(String id)` / `FindLevel(String id)` |
| levels | `Levels : IBaseCollection<LevelElement> { get; }` |
| identity | `ParentObject : Transaction { get; set; }`, `Name`, `Id`, `Module` |

`Artech.Patterns.WorkWith.FilterElement` — the node the issue asks about

| Member | Signature |
|---|---|
| add filter attribute | `FilterAttributeElement AddFilterAttribute()` / `AddFilterAttribute(String name)` |
| find filter attribute | `FilterAttributeElement FindFilterAttribute(String name)` |
| add condition | `ConditionElement AddCondition()` / `AddCondition(String value)` |
| find condition | `ConditionElement FindCondition(String value)` |
| collections | `Attributes : FilterAttributesElement`, `Conditions : ConditionsElement` |
| declare variables | `String DefineVariables(VariablesWrapper variables)` |

`Artech.Patterns.WorkWith.FilterAttributeElement` — settable `Name`, `Description`,
`Domain`, `DomainName`, `Default`, `AllValue`, `Prompt`, `InternalPath`; derived
`VariableName`, `Length`, `EnumeratedDomain`, `IsDateType`, `IsComboType`.

`Artech.Patterns.WorkWith.ConditionElement` — settable `Value`, `Parent`,
`InternalPath`.

Navigation is `WorkWithInstance.Levels[i] → LevelElement.Selection → SelectionElement.Filter`.
`TransactionElement` has **no** `Selection`: the selection hangs off the Level, which is
exactly the ambiguity the issue flagged. The disambiguating handles are
`FindLevel(id)`, `FindFilterAttribute(name)` and the per-element `InternalPath`, which
is the "identity by path/identifier returned by the read" the issue asked for.

## The schema agrees with the reported XML

From the shipped `WorkWithInstance.xml`:

```xml
<ElementType Name="Filter">
  <ChildrenElements>
    <ChildElement Name="attributes" ElementType="FilterAttributes" Multiple="false" Optional="false" />
    <ChildElement Name="conditions" ElementType="Conditions"      Multiple="false" Optional="false" />
  </ChildrenElements>
</ElementType>

<ElementType Name="FilterAttribute" KeyAttribute="name">
  <Attribute Name="name"        Type="code(Expressions)" NotNull="true" />
  <Attribute Name="description" Type="string" />
  <Attribute Name="domain"      Type="reference(Domain)" />
</ElementType>

<ElementType Name="Condition" KeyAttribute="value">
  <Attribute Name="value" Type="code(Conditions)" NotNull="true" />
</ElementType>
```

`attributes` and `conditions` are both `Optional="false"`, so a `Filter` always carries
both. The reporter's read snippet has both, and the shape matches node for node — so the
typed API and the serialized document are two views of one model, not two contracts.

## Why this should be a typed action, not a relaxed XML gate

The reporter is right that the current `PatternStructureChangeUnsupported` protection
is appropriate and should be preserved — it comes from `PatternXmlEditPlan`, which is a
pure preflight that refuses to let a raw XML payload author SDK-owned structure.

Now that a typed API exists for exactly this element, relaxing that gate would be the
wrong fix twice over: it would widen a raw-XML hole to cover a node the SDK already
offers typed methods for, and it would lose the validation the typed API makes free
(`FindFilterAttribute` for the duplicate check the issue asks for, `Domain` as a real
reference rather than a GUID string).

So the feasibility answer is positive and the design direction follows: a small typed
WorkWith operation resolving instance → level → selection → filter, validating before
mutating, previewing without Save, and reporting the saved definition separately from
any regenerated derived objects.

## What is not yet proven

- **Persistence.** The route is read from the installed surface. Whether
  `SaveTo(patternInstance)` followed by the SDK's instance save persists and re-reads as
  the reporter wants is not executed here: no KB in `C:\KBs` has a standard WorkWith
  applied, and mutating a user's KB is not authorized.
- **Regeneration.** Whether the derived XLS procedure regenerates from the new filter is
  a generator question, separate from authoring — and the issue already scopes that as a
  separate capability.
- **Exact overload shape under reflection.** The MCP resolves SDK members reflectively
  and tolerates drift between majors, so the call site needs the usual optional-member
  handling rather than a hard reference to `AddFilterAttribute(String)`.

None of that undermines the answer to the question that was asked: a candidate route
exists, it is typed, and it is the right one to build on.
