# Contributing to SilabsBgapi

Thank you for your interest in contributing! This guide will help you get started.

## Reporting Bugs

Open a [GitHub issue](https://github.com/Lynxal/silabs-bgapi-csharp/issues/new?template=bug_report.md) with:

- .NET version and OS
- NCP firmware version and XAPI file version
- Steps to reproduce
- Expected vs actual behavior
- Relevant logs or error messages

## Submitting Pull Requests

1. Fork the repository
2. Create a feature branch from `main`: `git checkout -b feature/your-feature`
3. Make your changes
4. Ensure all tests pass: `dotnet test`
5. Submit a pull request against `main`

### PR Guidelines

- Keep PRs focused on a single change
- Include tests for new functionality
- Update documentation if the public API changes
- Follow the existing code style (see below)

## Code Style

- **File-scoped namespaces** (`namespace Foo;`)
- **Nullable enabled** (`#nullable enable` is project-wide)
- **XML doc comments** on all public types and members
- **Async suffix** on async methods (`SendCommandAsync`, not `SendCommand`)
- **Sealed classes** where inheritance is not intended
- Existing patterns in the codebase take precedence over these guidelines

## Building

```shell
dotnet restore
dotnet build
dotnet test
```

## Project Structure

```
src/SilabsBgapi/          # Library source
tests/SilabsBgapi.Tests/  # Unit tests
examples/BasicUsage/      # Example application
```

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
