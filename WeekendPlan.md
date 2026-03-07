- [ ] Restart IIS: `iisreset /restart`
- [ ] Verify site loads: https://sentinelaisystems.net
- [ ] Submit test pilot application
- [ ] Monitor logs for errors

---

## GA4 & ANALYTICS UPDATES (If Time Permits)

- [ ] Update GA4 event: "join_launch_list" → "apply_pilot_program"
- [ ] Add new conversion goal: "Pilot Application Submitted"
- [ ] Update Clarity heatmap goals
- [ ] Set up alerts for form submission failures

---

## POST-DEPLOYMENT MONITORING

- [ ] Monitor email notifications for 24 hours
- [ ] Check WebLeads table for new pilot applications
- [ ] Review Clarity sessions for user confusion points
- [ ] Check GA4 for bounce rate on new pages
- [ ] Gather feedback from any test submissions

---

## BACKLOG (Future Iterations)

- [ ] Create dedicated /pilot landing page
- [ ] Add pilot partner logo section (once you have partners)
- [ ] Create pilot partner dashboard/portal
- [ ] Add calendar integration for pilot kickoff calls
- [ ] Create automated email sequence for pilot applicants
- [ ] Develop pilot onboarding checklist
- [ ] Create case study template for pilot partners

---

## NOTES & DECISIONS

**Messaging Principles:**
- Proven, not vaporware
- Co-development opportunity
- Limited spots (creates urgency)
- Clear path to full suite (later)

**Avoid:**
- Over-promising unavailable features
- Confusing "coming soon" vs "available now"
- Generic "smart factory" buzzwords
- Downplaying EM's standalone value

**Key CTAs:**
1. Primary: "Apply for Pilot Program"
2. Secondary: "See How It Works" / "Learn More"
3. Tertiary: "Talk to Us" (custom builds)

**Email Topics to Support:**
- PilotApplication (new, primary)
- LaunchList (keep for non-pilot interest)
- CustomBuild (keep for custom needs)

---

## QUICK REFERENCE

**Files Modified (Priority Order):**
1. ✅ Controllers\HomeController.cs (NormalizeTopic updated)
2. Views\Home\Index.cshtml
3. Views\Home\Contact.cshtml
4. Views\Home\Pricing.cshtml
5. Views\Shared\_Layout.cshtml
6. Views\Home\CoreSuite.cshtml
7. Views\Home\Platform.cshtml
8. Views\Home\About.cshtml
9. Views\Home\UseCases.cshtml
10. Views\Home\Resources.cshtml

**New Files to Create:**
- Views\Shared\_PilotBenefitsCard.cshtml (optional)
- Views\Shared\_PilotCtaStrip.cshtml (optional)

**Config Changes:**
- None required (Notification:Recipient already set)

**Database Changes:**
- None required (WebLeads table already supports PilotApplication topic)