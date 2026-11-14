# Rhino agent control plane

A workspace for directing agent work on Rhino models and reviewing the resulting proposals.

## Language

**Task**:
A unit of work assigned to an agent on a Rhino model. A completed task can produce a **Proposal** for human review.
A task owns its conversation, working document copy, editable scope, and agent worker while running.

**Proposal**:
An agent's suggested model changes submitted for human review. Completion of the producing **Task** does not mean the proposal has been accepted.
_Avoid_: PR, pull request (informal analogies)

**Task plan**:
A reviewable description of proposed **Tasks**, including their instructions and geometry scopes. The human can launch reviewed tasks together or individually; reviewing a plan does not accept the resulting **Proposals**.

**Editable scope**:
The objects or layers a **Task** is authorized to change in its document copy. Expanding this scope requires human approval.

**Read-only context**:
The rest of a **Task**'s document copy, available for inspection but outside its **Editable scope**.

**Orchestrator**:
An agent that proposes a **Task plan**, dividing a modeling request into scoped tasks and their dependencies for human review.

**Task dependency**:
A relationship in which a task needs a specific result from an upstream task as input. Depending on a proposal does not mean that proposal has been accepted into the shared model.

## Example dialogue

**Human:** Three tasks have finished with different facade options. Has the shared model changed?

**Developer:** Completion means the proposals are ready for review. You decide which proposal to accept into the shared model.
