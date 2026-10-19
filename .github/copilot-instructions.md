# Copilot Instructions

## Azure Guidelines
- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.

## HTML Rendering Guidelines
- In Razor views, a conditional attribute like @(condition ? null : "disabled") renders as a bare minimized HTML attribute (just `disabled`), not `disabled="disabled"`. When writing HTML-content test assertions against such output, match the bare attribute form (or assert on element absence when the whole markup block is conditionally omitted).

## Project-Specific Rules
- Prefer Razor Pages over Blazor or MVC when implementing internal assessment/report workflows, following existing Sentinel Razor Page conventions.
- Use the existing solution and `PostgresIntegrationTestFixture` for PostgreSQL integration tests; do not create temporary database-check projects.
