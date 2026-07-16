# CLAUDE.md

## Memory (memory-os)

This project is wired to the **canvas** product long-term memory (memory-os).
Resolve durable knowledge and in-flight context via these roots:

- **LTM root**: `C:\Users\Sipan\Documents\Work\Lynxal\canvas-ltm\index.md` (product: canvas)
- **STM root**: `project_memory/index.md`

Use `memory:recall` to retrieve knowledge, `memory:remember` for transient notes,
and `memory:propose` to durably record decisions (opens a PR against the LTM).
Local wiring lives in `.memory/ltm-config.md` (gitignored, machine-specific).
<!-- memory-os:end -->
