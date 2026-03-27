# Contributing to SilabsBgapi

Thank you for your interest in contributing! This guide explains how to get involved.

## Reporting Bugs

Open an issue using the **Bug Report** template. Include:

- .NET version and OS
- SilabsBgapi package version
- NCP firmware version and hardware model
- Steps to reproduce
- Expected vs actual behavior
- Relevant logs or stack traces

## Suggesting Features

Open an issue using the **Feature Request** template. Describe the problem you're solving and your proposed approach.

## Submitting Pull Requests

1. Fork the repository and create a branch from `main`
2. Make your changes
3. Ensure all tests pass: `dotnet test`
4. Ensure the build is clean: `dotnet build --configuration Release`
5. Write clear commit messages describing the change
6. Open a pull request against `main`

### Code Style

- File-scoped namespaces
- Nullable reference types enabled
- XML doc comments on all public APIs
- Follow existing patterns in the codebase

### Testing

- Add unit tests for new functionality
- Tests use xUnit, NSubstitute, and FluentAssertions
- Run the full suite before submitting: `dotnet test`

### Pull Request Guidelines

- Keep PRs focused on a single change
- Reference any related issues
- Describe what changed and why
- Include test coverage for new behavior

## Development Setup

```shell
git clone https://github.com/Lynxal/silabs-bgapi-csharp.git
cd silabs-bgapi-csharp
dotnet restore
dotnet build
dotnet test
```

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
