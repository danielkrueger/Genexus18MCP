# WorkWith (standard) derived-object regeneration - SDK feasibility, GeneXus 18

Answers the question in issue [#353](https://github.com/lennix1337/Genexus18MCP/issues/353):
for **standard** WorkWith on GX18, is there an overload or known context requirement
that can regenerate the derived objects of an **existing** pattern instance?

**Answer: there is a named candidate, and it is not one of the two overloads the code
already rejected.** It is `PatternEngine.GenerateInstanceObjects`, an `internal static`
entry point in `Artech.Packages.Patterns` that this MCP has never called. Every one of
its six arguments is obtainable from the installed SDK today, by reading alone, with no
vendor package and no missing dependency.

This is a **surface reading of the installed SDK, not an executed regeneration run.**
`GenericReapplySupported` is unchanged. See *What is not yet proven*.

## RESULT: regeneration works, and it does not go through reapply

This is the outcome of the experiment described at the end of this file, run on this
machine against `C:\KBs\KBTeste` on GeneXus **18.0.10.184260**. The answer to the issue's
question is **yes**, and it is not the candidate above.

**A plain `genexus_edit name=Trn353 part=PatternInstance` regenerates the derived family.**
No reapply, no IDE, no K2BTools, no `GenerateInstanceObjects` call, no reflection.

Protocol and evidence, one round trip per row. The changed property is
`instance/level/selection/@description`, an existing author-visible string that the
generated list renders as its title control - so its arrival in a *generated* control is
proof of regeneration, not of the instance edit being echoed somewhere:

| Step | instance `selection/@description` | `WWTrn353` | `ViewTrn353` |
|---|---|---|---|
| baseline | `Trn353s` | len 6070 | len 4066 |
| 1. forward | `ZZPROBE353` | contains probe, len 6073 | contains probe, len 4069 |
| 2. forward | `ZZPROBE354` | probe 353 gone, 354 present | probe 353 gone, 354 present |
| 3. revert | `Trn353s` | back to len **6070** | back to len **4066** |

The generated list's `TitleText` control carries the new caption:

```xml
<textblock controlName="TitleText" caption="ZZPROBE353" class="d52f9833-..." />
```

`Trn353General` (WebComponent) did **not** carry the property - expected, since the
selection description only feeds the list - so it is not evidence of a partial failure.

Every `versionToken` moved on each write, and step 3 returned both derived objects to
their exact baseline lengths, so this is a real round trip rather than a monotonically
growing document.

**A no-op instance write still moves the derived tokens.** Re-saving the unchanged
instance returned `WriteNoChange` and yet `WWTrn353`/`ViewTrn353` `versionToken` both
changed. So the trigger is *saving the instance*, not *changing it* - worth knowing before
anything relies on a token as evidence that regeneration was needed.

### What this does and does not settle

Settled: an existing standard WorkWith's derived objects **can** be regenerated headlessly
in this environment. The premise behind `GenericReapplySupported = false` - that headless
regeneration is unavailable for standard WorkWith - does not hold here. The recorded
negative evidence was **GX17 U4 + K2BTools 13.1** and concerned the two `ApplyPattern`
overloads; neither this environment nor this trigger path was covered by it.

Not settled: **the mechanism.** Regeneration was not observed to be MCP-driven -
`SetPatternApplyOnSave` is called only on the WorkWithPlus path
(`DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface`), and nothing in
this build sets an equivalent for standard patterns. The leading hypothesis is the SDK's
own instance save hook: `PatternInstance` exposes `OnBeforeSaveKBObject` and
`OnAfterSaveKBObject`, and the instance root carries `updateTransaction="Apply WW Style"`.
That is a hypothesis from reading; it was not isolated, and the experiment did not log
which callback did the work. **Do not build on the mechanism until it is identified** -
only the observable behaviour is established.

## RESULT 2: the reapply route was tested, and the refusal is confirmed

Follow-up experiment, same KB and the same GeneXus build, using the reporter's
precondition discipline: establish a **known pending difference** first, then run reapply
and re-read instance *and* derived objects with content and hashes. A changed save token
alone is not accepted as proof either way.

**The pending difference.** Two corruption routes were tried first and both are blocked by
existing safety guards, which is itself worth recording: hand-editing the generated
`WW<Trn>` WebForm is refused with `K2BDesignerEditUnsupported` (it would change K2BTools
designer grid metadata), and deleting a generated object is refused by the SDK. So the
difference was created legitimately instead - `NewAttr353` (Character(10)) was added to the
Transaction `Trn353`, and confirmed present on the Transaction while absent from the
instance and from all three derived objects. Nothing had touched the instance, so
regeneration could not already have happened.

**Control, to prove the measurement can detect regeneration.** In the same run, the
instance-save path - measured above to regenerate - changed `selection/@description` to
`CTRLPROBE`; the value appeared in the generated `TitleText` control and both derived
`versionToken`s moved. The apparatus detects regeneration when it happens.

**Reapply, with no settings.** `PatternEngineApplyFailed` -
`NullReferenceException`, rethrown from `ReflectionPatternEngineAdapter.ReapplyPattern`
(`PatternEngineAdapter.cs:268`), which is the SDK's own exception surfaced through
`TargetInvocationException`. So the throw is inside
`PatternEngine.ApplyPattern(PatternInstance, ApplySettings)` on **GX18**, without
K2BTools - the GX17 U4 + K2BTools 13.1 finding reproduces on a different major and without
that package.

**And the cause of the throw is ours.** `ReapplyPattern` passes `null` for `ApplySettings`
when the caller supplies none (`PatternEngineAdapter.cs:245` - "pass null, which the SDK
treats as 'use defaults'"). That assumption is what the SDK dereferences and throws on. It
is not a missing or unlicensed package, and the old hint said to go check licensing.

**Reapply, with settings.** `ApplyPattern(reapply=true, settings={IsFullGeneration: true,
ForceSave: true})` returned **`PatternApplied`** - no throw. And it regenerated nothing:

| | `WW<Trn>` | `View<Trn>` | `<Trn>General` | pending difference |
|---|---|---|---|---|
| before | 6070 | 4066 | 2434 | open |
| after | 6070 | 4066 | 2434 | **still open** |

Byte-identical, and the known pending difference survived. So the recorded negative result
is **confirmed, not inherited** - on a second major, without K2BTools, against a
discriminator rather than a token change.

**The consequence is the important part: the route must stay refused.** The
`NullReferenceException` is fixable on our side by not passing null settings, and fixing it
would produce `PatternApplied` while every derived object sits at its previous version -
trading a loud refusal for a silent no-op. The refusal is doing real work, which is the
opposite of what the #353 diagnosis suggested. The bug this did find is the *diagnosis*,
not the gate: the `PatternEngineApplyFailed` hint told callers to verify the pattern
package and KB state, for an exception this build causes itself.

`GenericReapplySupported` remains `false`.

### Limitation of the pending difference

The discriminator is a Transaction attribute the **instance does not reference** - the
`PatternInstance` was never told to include it. A correct regenerator might legitimately
not have propagated it into the grids, so "did not regenerate" rests primarily on *nothing
changed at all* - identical lengths across all three derived objects, with the
instance-save control in the same run proving the measurement was live - rather than on
this particular difference being resolved. A stronger discriminator needs drift in an
object reapply would have rewritten, and every route to create that drift is currently
blocked by a safety guard or the SDK.


### A build trap that produced three false readings

Worth writing down because it cost three separate wrong conclusions during this work, and
each looked like a real defect in the code under test.

`Copy-Item` restoring a file during a mutation run leaves an **older** mtime than the
build output it is meant to replace. MSBuild then considers the output current and skips
the copy, so the *source* is correct while the *binary under test* is the mutated one.
Two layers are involved and both have to be forced:

- `dotnet build GxMcp.Worker.csproj -t:Rebuild` refreshes the Worker's own `bin`, and
  nothing else. It does **not** refresh the copy of `GxMcp.Worker.exe` inside
  `GxMcp.Worker.Tests\bin\Debug\net48\`, which is what `dotnet test` actually loads.
- The test project must therefore be rebuilt too: `dotnet build
  GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -t:Rebuild`. `-t:Rebuild` is accepted by
  `dotnet build` and **not** by `dotnet test`, which fails the run with an unrecognised
  option and prints no result line at all.

Symptom to recognise: a test that passes in isolation and fails in the full run, where the
failure message quotes a string that is *not* in the source. The message is the binary
talking, not the source. Here it read `... every derived object at at its previous version`
- a doubled word that exists only in a mutation - which is what finally located it.



`derivedObjectRegeneration` is emitted from a branch that requires the object's pattern to
resolve (`currentPattern != null && !IsWorkWithPlus`). Observed live: on a write where the
pattern did not resolve, the field is **absent** rather than present-and-honest. That is
still an improvement on the previous `generatedObjectsRegenerated: false`, which was
emitted as a flat fact - silence is not a false claim - but it is not the same as a
guaranteed field, and a caller must not branch on its presence. The final ship-check
confirmed the regeneration behaviour with the field absent. Making the field
unconditional belongs to the same follow-up as measuring regeneration properly.



The issue noted that `firstApply.supported = true` was "a declared capability, not
first-apply WorkWith/GX18 observed by us". That gap is now closed, and it is the reason
the regeneration question is sharper than it looked:

A first apply of **standard** WorkWith onto a Transaction, on this machine, GeneXus
**18.0.10.184260**, through the generic engine route, produced the derived family
headlessly - no IDE, no K2BTools:

| Object | Type | Role |
|---|---|---|
| `WWTrn353` | WebPanel | the selection list |
| `ViewTrn353` | WebPanel | the detail view |
| `Trn353General` | WebComponent | the general component |

(Transaction `Trn353`, pattern `WorkWith` 1.2, `parentObjects: ["Transaction"]`. No
`<Trn>Wrapper` and no export procedures appeared - those are settings/template dependent,
which is a separate question from whether generation happens at all.)

So the engine **can** run headlessly in this environment and **can** materialise a
standard WorkWith family. That does not prove the candidate works - generation on first
apply and regeneration over an existing instance are different operations - but it removes
"the SDK cannot generate WorkWith headlessly" from the table of explanations, leaving only
"the reapply path re-saves instead of regenerating". `reapply.supported` remains `false`
and correctly so; the route was refused, not silently accepted.

The regeneration experiment below then found something stronger still: saving the instance
regenerates, with no reapply involved. Read that section before the candidate - on this
environment the candidate is not the interesting answer.

## Where the evidence comes from

Reflection over GeneXus **18.0.10.184260**, `C:\Program Files (x86)\GeneXus\GeneXus18`:

- `Artech.Packages.Patterns.dll` - the Artech pattern engine (335 types reachable)
- `Packages\Patterns\WorkWith\Artech.Patterns.WorkWith.dll` - the standard WorkWith
  package (238 types)

Note the path, again: pattern packages live under the **major** directory,
`GeneXus\GeneXus18\Packages\Patterns`, not `GeneXus\Packages\Patterns`. Standard
WorkWith is a built-in pattern and **is** installed. `WorkWithPlus` is a DVelop package
and is **not** present on this machine, so nothing below depends on it.

## The candidate

```csharp
namespace Artech.Packages.Patterns
{
    static class PatternEngine          // internal
    {
        internal static bool GenerateInstanceObjects(
            Artech.Packages.Patterns.Custom.IPatternBuildProcess buildProcess,
            Artech.Packages.Patterns.Engine.PatternModel             patternModel,
            Artech.Packages.Patterns.Objects.PatternInstance         instance,
            Artech.Packages.Patterns.Engine.InstanceObjects          instanceObjects,
            Artech.Packages.Patterns.Engine.ApplySettings            settings,
            Artech.Packages.Patterns.Engine.ApplyResults             results);
    }
}
```

It returns `bool`, so a caller can report the SDK's own verdict instead of inferring one
from a re-read. Per-object materialisation is the same shape one level down:

```csharp
namespace Artech.Packages.Patterns.Engine
{
    static class PatternInstanceGenerator
    {
        static InstanceObject GenerateObject(
            IPatternBuildProcess buildProcess, PatternModel patternModel,
            PatternObject patternObject, PatternInstance instance,
            PatternInstanceElement element, ApplySettings settings);
    }
}
```

The reporter's reading of `PatternEngine`'s public surface is confirmed exactly, and
`GenerateInstanceObjects` is a **third** entry point alongside the two they quoted:

```
PatternDefinition AddPatternDefinition(String)
Boolean           ApplyPattern(PatternInstance, ApplySettings)   <- rejected: NRE headless
Void              ApplyPattern(KBObject, PatternDefinition)      <- rejected: re-saves only
Boolean           GenerateInstanceObjects(...)                  <- never tried
PatternDefinition GetPatternDefinition(Guid)
PatternDefinition GetPatternDefinition(String)
```

## Every argument is obtainable

| # | Argument | How to get it | Access |
|---|---|---|---|
| 1 | `IPatternBuildProcess` | `new Artech.Patterns.WorkWith.WorkWithPattern()` → `Initialize()` → `GetBuildProcess()` | **all public**; parameterless ctor |
| 2 | `PatternModel` | ctor `(KBModel)` over the open KB's model | type internal, ctor internal → reflection |
| 3 | `PatternInstance` | the existing instance; the adapter already resolves this | public |
| 4 | `InstanceObjects` | ctor `(PatternInstance)`; the instance's own `get_Objects()` / `get_GeneratedObjects()` are the likely feed | type public, ctor internal → reflection |
| 5 | `ApplySettings` | `new` - `IsFullGeneration`, `ForceSave`, `ForceApply`, `ForceDefault`, `ImportingInstance`, `OutputLevel` | **all public `{get;set;}`** |
| 6 | `ApplyResults` | ctor `(PatternInstance)`; carries `Errors`, `Successful`, `Pattern`, `Instance` | type public, **ctor public** |

Nothing here needs a key, a licensed feature, or an absent package.

## Why the two rejected overloads can fail while this one might not

The reporter asked for a *hypothesis* identifying "an existing property whose change could
produce a verifiable difference", and this is the strongest one available by reading.

`IPatternBuildProcess` is Artech's shared contract, and the generation hooks on it are
**pre/post callbacks around** materialisation, not the materialisation:

```
ShouldBuild(PatternInstance)                       -> bool?
BeforeStartBuild(PatternInstance)
AfterImportResources(PatternInstance)
BeforeGenerateObjects(PatternInstance, IBaseCollection<InstanceObject>)   <- the generation hook
BeforeGenerateObject(PatternInstance, InstanceObject)
BeforeSaveObjects(PatternInstance, InstanceObjects)
AfterSaveObjects(PatternInstance, InstanceObjects)
AfterEndBuild(PatternInstance)
UpdateParentObject(KBObject, PatternInstance)      <- projection onto the parent ONLY
```

So `UpdateParentObject` cannot regenerate a derived family by itself - it writes the
pattern onto the bound parent. Something has to *drive* the loop, and
`GenerateInstanceObjects` is the driver: it receives the `InstanceObjects` collection and
calls back into the build process around each one.

**This also explains the asymmetry the reporter could not resolve.** WorkWithPlus
regenerates headlessly through `UpdateParentObject` because for WWP-on-a-WebPanel the
*generated object is the parent's own WebForm projection* - there is no separate family to
materialise, so projecting is the whole job. Standard WorkWith on a Transaction generates
`WW<Trn>`, `<Trn>General`, export procedures and so on as **separate KBObjects**, which
needs the driver, not the projection hook. Same contract, different job.

**Hypothesis, not established:** `ApplyPattern(KBObject, PatternDefinition)` produced
"re-saves the instance, derived objects keep their previous version" on GX17 U4 because
it does not route through `GenerateInstanceObjects`, or routes through it with
`ApplySettings.IsFullGeneration == false`. That is consistent with the recorded symptom
and with the existence of that property, but nothing here demonstrates it. It is the
first thing an experiment should log.

## Structural consequence for the code

`GenericReapplySupported = false` is a claim about **two specific overloads**, and the
comment above it says so. It is not a claim that the SDK cannot regenerate. The switch
and its reason string are worded as a blanket statement about "the pattern engine", which
is broader than the evidence supports and is what makes the diagnostic read as a
dead end. Fixing the wording is a separate, cheap change from testing the candidate -
see the options in the issue.

## What is not yet proven

- **No regeneration was executed.** Everything above is reading installed metadata.
- **The candidate may be the same code path that already fails.** This is the strongest
  argument against the finding and it is not excluded. If
  `ApplyPattern(PatternInstance, ApplySettings)` reaches
  `GenerateInstanceObjects` internally, then calling the driver directly reproduces the
  recorded `NullReferenceException` and locates nothing new - the only difference would be
  the `ApplySettings` values. What makes it worth a bounded test rather than dismissal is
  the asymmetry argued above: the two rejections were observed on **GX17 U4**, while this
  is **GX18.0.10.184260**, and `ForceSave` / `ForceApply` / `IsFullGeneration` are
  documented-enough properties that "which settings" is a distinct question from "which
  overload". A negative result must therefore record the argument provenance, not just the
  exception - otherwise it cannot be told apart from the existing evidence.
- The evidence that moved the switch was **GX17 U4 + K2BTools 13.1**. This reading is
  **GX18.0.10.184260** and standard WorkWith only. It is not the same combination, so it
  neither confirms nor refutes the prior result; it locates a route the prior result did
  not cover.
- The `InstanceObjects` source is inferred, not traced. The ctor is
  `(PatternInstance)`; whether the engine expects a fresh one or one derived from
  `instance.get_Objects()` is the first unknown an experiment resolves.
- `PatternModel` wraps a `KBModel`; which `KBModel` the engine wants (the open KB's) is
  untested.
- `GenerateInstanceObjects` is `internal`. Reflection reaches it, and this codebase
  already reflects over pattern internals, but a vendor could change the signature
  between majors - so any adoption must fail closed on a signature mismatch rather than
  degrade to a silent no-op.

## The experiment, if it is run

The reporter's three verifications are the right ones. Concretely, on a disposable GX18
KB with a Transaction carrying a standard WorkWith:

1. Record the derived family with per-object content hashes, via
   `PatternInstance.get_GeneratedObjects()` and the KB catalog.
2. Establish the precondition by reading: change one existing instance property through a
   permitted route, re-read, and show the difference is pending. Do **not** save in the
   IDE to create that difference - the IDE would regenerate along with it, which is
   exactly the confound the reporter named.
3. Call `GenerateInstanceObjects` with `IsFullGeneration` recorded either way, log every
   argument's provenance and any exception, then re-read **instance and derived objects**
   and compare hashes. A changed save token alone proves nothing.
4. Repeat for no-op/idempotency.
5. Conclude per environment and route: regeneration proven, bounded failure, or
   inconclusive. Absence of proof is not proof of impossibility - and the negative result
   must say which combination it covers, or it repeats this issue.

Never on a business KB. Specify/Build/Reorg are not implicit effects; if the candidate
turns out to need them, that is a separate declared step.
