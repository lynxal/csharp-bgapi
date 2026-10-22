# CLAUDE.md

## Memory (memory-os)
- Collective Memory (CM) root: `<cm_local_path>/index.md`, where `cm_local_path` is read from `.memory/cm-config.md` (gitignored, machine-specific)
- Operational Memory (OM) root: `operational_memory/index.md`

Memory routing — these rules hold whether or not the user names a skill:
- A question about the product, a project, a standard, a component, an error, a term, or where work left off: run `memos:recall` first; read code only for what memory does not answer.
- Feature-sized development work — new user-visible behaviour, a new endpoint, entity, contract or integration, or a change across several components — is implemented only against an active spec under `operational_memory/specs/`. When none matches: stop, say so, and run `memos:shape-spec` before writing any code. The user may waive this for the turn by saying so in plain words ("no spec, proceed"); state that the waiver was taken. Bug fixes, behaviour-preserving refactors, config, typo and test-only changes are not gated. Unsure whether the work is feature-sized: treat it as feature-sized.
- A durable fact learned while working (a decision, a convention, what a component is for): offer `memos:propose`.
- A passing note or an idea not yet specced: offer `memos:remember`.
- A product term nobody has defined keeps coming up: offer `memos:glossary`.
- Asked to review a pull request or a branch: run `memos:review-code`.
<!-- memory-os:end -->
