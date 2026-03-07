# Weekend Plan - EM Pilot Launch Pivot

## FRIDAY (Setup & Planning)
- [x] Strategy pivot decided: EM Module → Pilot Partners
- [ ] Create git branch: `git checkout -b pivot-to-em-pilot`
- [ ] Review all open files and identify content changes needed
- [ ] Backup current production deployment

---

## SATURDAY (Core Messaging & Critical Pages)

### Homepage (Views\Home\Index.cshtml) - PRIORITY 1
- [ ] Update hero title: Focus on EM Module (not full suite)
- [ ] Update hero subtitle: Add pilot partner recruitment message
- [ ] Change primary CTA: "Apply for Pilot Program" (not "Join Launch List")
- [ ] Add new "Pilot Benefits" section below hero
- [ ] Remove/soften multi-module messaging
- [ ] Update countdown section: "Pilot applications open" instead of "Launch"
- [ ] Test all CTAs and links

### Contact Form (Views\Home\Contact.cshtml + Controllers\HomeController.cs)
- [x] Add "PilotApplication" to NormalizeTopic method
- [ ] Update Contact GET: Add heading for "Apply for Pilot Program"
- [ ] Update Contact GET: Add intro text emphasizing pilot partnership
- [ ] Test form submission with new topic
- [ ] Verify email notification reaches michael@sentinelaisystems.net

### Pricing Page (Views\Home\Pricing.cshtml)
- [ ] Add NEW "Pilot Partner" tier card at top
  - Limited spots (5-10 partners)
  - 50% discount for 12 months
  - Co-design privileges
  - Direct engineering access
- [ ] Rename tiers: "EM Standard" (current Standard)
- [ ] Gray out or hide Advanced/Premium/Enterprise
- [ ] Add "Coming 2026" labels to future modules
- [ ] Update CTA buttons to point to pilot application

### Navigation & Layout (Views\Shared\_Layout.cshtml)
- [ ] Update primary nav CTA: "Apply for Pilot" button
- [ ] Consider renaming "Core Suite" nav item to "Emergency Maintenance"
- [ ] Update footer product description
- [ ] Test mobile nav

---

## SUNDAY (Supporting Pages & Polish)

### Core Suite Page (Views\Home\CoreSuite.cshtml)
**Decision:** Repurpose as EM deep-dive or keep as roadmap?
- [ ] Option A: Rename to EmergencyMaintenance.cshtml and focus 100% on EM
- [ ] Option B: Keep name, show EM as "Available Now", others as "Coming Soon"
- [ ] Update countdown timer messaging
- [ ] Update all CTAs to pilot application
- [ ] Remove or soften promises about future modules

### Platform Page (Views\Home\Platform.cshtml)
- [ ] Lead section: Emphasize EM Module capabilities
- [ ] Architecture section: Show extensibility without over-promising
- [ ] Add "Start with EM, grow with us" messaging
- [ ] Update feature list: Focus on what's available now
- [ ] Test all links and CTAs

### About Page (Views\Home\About.cshtml)
- [ ] Add new section: "Why We're Starting with EM"
- [ ] Add placeholder for pilot partner testimonials
- [ ] Emphasize commitment to co-development
- [ ] Update team/company story to align with focused launch

### Use Cases Page (Views\Home\UseCases.cshtml)
- [ ] Focus use cases on EM Module scenarios
- [ ] Add pilot partner success story placeholders
- [ ] Update CTAs to pilot application
- [ ] Remove/soften multi-module use cases

### Resources Page (Views\Home\Resources.cshtml)
- [ ] Update any content mentioning "full suite launch"
- [ ] Add pilot program FAQ section
- [ ] Update download/resource links
- [ ] Test all external links

### New Components (Create if needed)
- [ ] Create _PilotBenefitsCard.cshtml partial (reusable component)
- [ ] Create _PilotCtaStrip.cshtml (alternative to existing CTA strip)
- [ ] Update existing _TierCard.cshtml to support "Pilot" tier styling

---

## TESTING & DEPLOYMENT (Sunday Evening)

### Testing Checklist
- [ ] Test all navigation flows
- [ ] Test contact form with "pilot-application" topic
- [ ] Verify email notifications work (check michael@sentinelaisystems.net)
- [ ] Test on mobile (responsive design)
- [ ] Test all CTA buttons lead to correct pages
- [ ] Verify no broken links
- [ ] Test GA4 tracking (if enabled)
- [ ] Check Clarity session recordings (if enabled)

### Content Audit
- [ ] Search codebase for "Manufacturing Core Suite" → decide keep or replace
- [ ] Search for "launch" → update to "pilot" where appropriate
- [ ] Search for "coming soon" → ensure consistency
- [ ] Search for "early access" → update to "pilot partner"
- [ ] Verify no contradictory messaging across pages

### Deployment
- [ ] Build and test locally one more time
- [ ] Publish to dev machine: `dotnet publish -c Release`
- [ ] Backup VM web.config
- [ ] Robocopy to VM (exclude web.config and appsettings.Production.json):

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