# Fleet Portal Conventions
- Next.js, TypeScript, React + Tailwind CSS (sole styling system, AL-52 — shared
  @mageride/tailwind-preset, no CSS-in-JS/MUI/Bootstrap)
- Wireframe reference: specs/wireframes/web_fleet.html (13 screens, SCR-FP-001…012)
- Sign-in is Email+Password / Google / Apple (AL-07) — never Phone OTP, never MFA
- A fleet operates Mode A and/or Mode B only. Never surface a Mode C option (AL-03)
- Every read is org-scoped; a cross-org read must be impossible from the client and refused
  by the server. Owner / Manager / Viewer sub-roles gate the UI — a Viewer sees no mutating control
- API calls go through fleet-svc (billing via fleet-billing-svc)
- npm workspace member `@mageride/fleet-portal` under portals/
- Verify: `npm --prefix portals run build -w @mageride/fleet-portal`
