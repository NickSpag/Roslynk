---
tool: apply_code_action
---
Applies actionId from get_code_actions, re-resolving at its original position; retains no action state.

Errors: Conflict if the action no longer resolves (rerun get_code_actions); Invalid for a
malformed actionId. Adding usings/members to Razor-generated
scaffolding may be unmappable.

Additional header: action.

## Parameters

- solutionId: Solution handle.
- actionId: actionId from get_code_actions.
- checkOnly: Preview only. (default: false)
