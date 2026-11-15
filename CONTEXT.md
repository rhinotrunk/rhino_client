# Rhino agent control plane

A workspace for directing agent work on Rhino models and reviewing the resulting proposals.

## Language

**Session**:
A modeling exploration that starts with a description and an orchestrator-led conversation. A session owns its task plan and one or more **Tasks**, including their dependencies and proposals.

**Task**:
A unit of work within one **Session**, assigned to an agent on a Rhino model. A completed task can produce a **Proposal** for human review.
A task owns its conversation, working document copy, editable scope, and agent worker while running.

**Proposal**:
An agent's suggested model changes submitted for human review. Completion of the producing **Task** does not mean the proposal has been accepted.
Once opened for review it is a **Pull request**: the task's result on its own agent branch, awaiting a merge or close decision.

**Pull request**:
A task's saved result committed to its own agent branch and offered for merge into the model's branch. Opening one does not change the model; merging it does, and closing it leaves the model untouched.

**Task plan**:
A reviewable description of proposed **Tasks**, including their instructions and geometry scopes. The human can launch reviewed tasks together or individually; reviewing a plan does not accept the resulting **Proposals**.

**Editable scope**:
The geometry a **Task** is authorized to change in its document copy: either the whole copy or selected objects or layers. Expanding this scope requires human approval.

**Read-only context**:
The rest of a **Task**'s document copy, available for inspection but outside its **Editable scope**.

**Orchestrator**:
The agent that coordinates a **Session**, turning its description into a reviewable **Task plan** and helping refine or extend that plan.

**Task dependency**:
A relationship in which a task needs a specific result from an upstream task as input. Depending on a proposal does not mean that proposal has been accepted into the shared model.

## Example dialogue

**Human:** Three tasks have finished with different facade options. Has the shared model changed?

**Developer:** Completion means the proposals are ready for review. You decide which proposal to accept into the shared model.
