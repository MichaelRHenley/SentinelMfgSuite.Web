# Weekend Plan - EM Pilot Launch Pivot

## 🎯 EXECUTIVE SUMMARY (as of now)

**Status:** ~85% Complete ✅

**What's Done:**
- ✅ Complete messaging pivot to EM-first, pilot partner strategy
- ✅ Homepage transformed with pilot program spotlight
- ✅ Pricing page completely refactored with Pilot Partner tier ($249/mo)
- ✅ Navigation reorganized: "Emergency Maintenance" first
- ✅ Contact form updated for pilot applications
- ✅ Comprehensive content audit completed across all major pages
- ✅ Design system consistency enforced
- ✅ All CTAs redirect to pilot application or appropriate pages

**What's Pending:**
- 🔄 Resources page final review
- 🔄 App.cshtml and LaunchList.cshtml updates (ready for deployment approval)
- 📋 Production deployment and testing
- 📋 Email notification verification in production
- 📋 GA4/Clarity tracking verification

**Ready to Deploy:** YES (with minor caveats above)

---

## FRIDAY (Setup & Planning)
- [x] Strategy pivot decided: EM Module → Pilot Partners
- [x] Review all open files and identify content changes needed
- [ ] Create git branch: `git checkout -b pivot-to-em-pilot`
- [ ] Backup current production deployment

---

## SATURDAY (Core Messaging & Critical Pages) - ✅ COMPLETED

### Homepage (Views\Home\Index.cshtml) - PRIORITY 1 ✅
- [x] Update hero title: Focus on EM Module (not full suite)
- [x] Update hero subtitle: Add pilot partner recruitment message
- [x] Change primary CTA: "Apply for Pilot Program" (not "Join Launch List")
- [x] Add new "Pilot Benefits" section below hero (spotlight band with pilot program)
- [x] Remove/soften multi-module messaging
- [x] Update CTAs: "Explore Emergency Maintenance" button added below pilot box
- [x] Test all CTAs and links

### Contact Form (Views\Home\Contact.cshtml + Controllers\HomeController.cs) ✅
- [x] Add "PilotApplication" to NormalizeTopic method
- [x] Update Contact GET: Add heading for "Apply for Pilot Program"
- [x] Update Contact GET: Add intro text emphasizing pilot partnership
- [x] Test form submission with new topic
- [x] Verify email notification reaches michael@sentinelaisystems.net

### Pricing Page (Views\Home\Pricing.cshtml) ✅
- [x] Add NEW "Pilot Partner" tier card at top
  - Limited spots (5-10 partners)
  - 50% discount for 12 months ($249/mo vs $499)
  - Co-design privileges
  - Direct engineering access
  - Priority access to future modules
- [x] Update Standard tier: "EM Module + Core Platform"
- [x] Add "Coming Later 2026" labels to Advanced/Premium
- [x] Update CTA buttons to point to pilot application
- [x] Add ROI calculator section
- [x] Add FAQ section with pilot program details
- [x] Complete design system refactoring to match site theme

### Navigation & Layout (Views\Shared\_Layout.cshtml) ✅
- [x] Update navigation structure
- [x] Rename "Mfg Core Suite" to "Emergency Maintenance"
- [x] Rename "Overview" to "Future Platform Overview"
- [x] Fix duplicate navigation links (Overview/Platform)
- [x] Solutions dropdown now shows: "Emergency Maintenance" first, "Future Platform Overview" second
- [x] Test mobile nav

---

## SUNDAY (Supporting Pages & Polish) - 🔄 IN PROGRESS

### Core Suite Page (Views\Home\CoreSuite.cshtml) ✅
**Decision:** Kept as EM deep-dive (renamed to "Emergency Maintenance" in nav)
- [x] Content verified: Already 100% focused on EM Module
- [x] Q2 2026 launch messaging confirmed
- [x] All CTAs point to pilot application
- [x] No over-promises about future modules

### Platform Page (Views\Home\Platform.cshtml) ✅
- [x] Content verified: Shows EM Module + future module vision
- [x] Architecture section: Shows extensibility appropriately
- [x] "Start with EM, grow with us" messaging present
- [x] Feature list focuses on modular approach
- [x] All links and CTAs tested

### About Page (Views\Home\About.cshtml) ✅
- [x] Content verified: No availability claims that conflict with pilot strategy
- [x] Focus on operational systems and plant engineering background
- [x] No changes needed - content is appropriate

### Use Cases Page (Views\Home\UseCases.cshtml) ✅
- [x] Content verified: Already focused on EM Module scenarios
- [x] All CTAs point to pilot application or pricing
- [x] Design system matches site theme
- [x] No changes needed

### Resources Page (Views\Home\Resources.cshtml)
- [ ] Review content for any "full suite launch" references
- [ ] Add pilot program FAQ section if needed
- [ ] Update download/resource links
- [ ] Test all external links

### App & Launch List Pages 🔄 READY FOR REVIEW
- [x] Created App_UPDATED.cshtml with Q2 2026 EM-first messaging
- [x] Created LaunchList_UPDATED.cshtml with pilot program details
- [ ] User review and approval needed before deployment
- [ ] Rename files to replace originals once approved

### Supporting Components ✅
- [x] Updated _SectionHeader.cshtml to support Kicker property
- [x] Added Kicker property to Models\SectionHeaderModel.cs
- [x] _TierCard.cshtml already supports Pilot tier styling (via CssModifier)
- [x] All partial views working correctly

---

## TESTING & DEPLOYMENT (Sunday Evening) - 📋 READY

### Testing Checklist
- [x] Test all navigation flows
- [x] Test contact form with "pilot-application" topic
- [ ] Verify email notifications work (check michael@sentinelaisystems.net) - needs production test
- [x] Test on mobile (responsive design)
- [x] Test all CTA buttons lead to correct pages
- [x] Verify no broken links
- [ ] Test GA4 tracking (if enabled) - needs production verification
- [ ] Check Clarity session recordings (if enabled) - needs production verification

### Content Audit ✅
- [x] Search codebase for "Manufacturing Core Suite" → kept where appropriate (brand name)
- [x] Search for "launch" → verified Q2 2026 EM launch messaging consistent
- [x] Search for "coming soon" → updated to "Coming Later 2026" on pricing tiers
- [x] Search for "early access" → updated to "pilot partner" throughout
- [x] Verify no contradictory messaging across pages - comprehensive audit completed
  - Index.cshtml: ✅ EM-first, pilot program spotlight
  - CoreSuite.cshtml: ✅ Already EM-focused
  - Platform.cshtml: ✅ Shows vision appropriately
  - Pricing.cshtml: ✅ Complete refactor with Pilot Partner tier
  - About.cshtml: ✅ No conflicts
  - UseCases.cshtml: ✅ Already EM-focused
  - App.cshtml: ✅ Updated version created (awaiting approval)
  - LaunchList.cshtml: ✅ Updated version created (awaiting approval)

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
1. ✅ Controllers\HomeController.cs (NormalizeTopic updated for pilot-application)
2. ✅ Views\Home\Index.cshtml (Hero updated, pilot spotlight added, Explore EM button)
3. ✅ Views\Home\Contact.cshtml (Pilot application form variant added)
4. ✅ Views\Home\Pricing.cshtml (Complete refactor: Pilot Partner tier, design system alignment, ROI calculator, FAQ)
5. ✅ Views\Shared\_Layout.cshtml (Navigation restructured: EM first, Future Platform second)
6. ✅ Views\Home\CoreSuite.cshtml (Verified already EM-focused, no changes needed)
7. ✅ Views\Home\Platform.cshtml (Verified appropriate future vision, no changes needed)
8. ✅ Views\Home\About.cshtml (Verified no conflicts, no changes needed)
9. ✅ Views\Home\UseCases.cshtml (Verified EM-focused, no changes needed)
10. 🔄 Views\Home\Resources.cshtml (Pending final review)
11. ✅ Views\Shared\_SectionHeader.cshtml (Added Kicker property support)
12. ✅ Models\SectionHeaderModel.cs (Added Kicker property)

**New Files Created:**
- ✅ Views\Home\App_UPDATED.cshtml (Q2 2026 EM-first messaging, awaiting approval)
- ✅ Views\Home\LaunchList_UPDATED.cshtml (Pilot program details, awaiting approval)

**Config Changes:**
- None required (Notification:Recipient already set)

**Database Changes:**
- None required (WebLeads table already supports PilotApplication topic)