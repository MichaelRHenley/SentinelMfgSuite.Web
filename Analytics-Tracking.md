# Analytics Tracking Documentation
## Sentinel Manufacturing Suite Website

**Last Updated:** December 2024  
**GA4 Property ID:** G-7R9BEEH432  
**Microsoft Clarity Project ID:** ukl6dgxpfg

---

## 📊 Table of Contents
1. [Analytics Setup](#analytics-setup)
2. [Page View Tracking](#page-view-tracking)
3. [Conversion Events](#conversion-events)
4. [Navigation Events](#navigation-events)
5. [Engagement Events](#engagement-events)
6. [UTM Parameter Capture](#utm-parameter-capture)
7. [Microsoft Clarity Setup](#microsoft-clarity-setup)
8. [Event Categories & Labels](#event-categories--labels)
9. [Recommended GA4 Conversions](#recommended-ga4-conversions)
10. [Testing Checklist](#testing-checklist)

---

## 🔧 Analytics Setup

### Google Analytics 4 (GA4)
**Implementation Location:** `Views/Shared/_Layout.cshtml` (lines 41-48)

```javascript
<!-- Google tag (gtag.js) -->
<script async src="https://www.googletagmanager.com/gtag/js?id=G-7R9BEEH432"></script>
<script>
    window.dataLayer = window.dataLayer || [];
    function gtag() { dataLayer.push(arguments); }
    gtag('js', new Date());
    gtag('config', 'G-7R9BEEH432');
</script>
```

**What's Tracked Automatically:**
- Page views (all pages)
- Session starts
- First visits
- User engagement
- Scroll depth (90% threshold via GA4 enhanced measurement)
- Outbound clicks (enhanced measurement)
- Site search (enhanced measurement, if applicable)
- Video engagement (enhanced measurement, if applicable)
- File downloads (enhanced measurement)

---

## 📄 Page View Tracking

All page views are tracked automatically by GA4. The following pages are live:

| Page | URL Path | Purpose |
|------|----------|---------|
| Homepage | `/` | Company overview & pilot program spotlight |
| Emergency Maintenance | `/Home/CoreSuite` | EM Module deep-dive |
| Future Platform Overview | `/Home/Platform` | Full platform vision |
| Use Cases | `/Home/UseCases` | Industry scenarios |
| Pricing | `/Home/Pricing` | Subscription tiers & pilot partner program |
| About | `/Home/About` | Company background |
| Resources | `/Home/Resources` | Documentation & guides |
| Contact | `/Home/Contact` | Lead capture form |
| Launch List | `/Home/LaunchList` | Email signup form |
| App | `/Home/App` | Product landing page |

---

## 🎯 Conversion Events

### 1. Apply for Pilot Program
**Event Name:** `apply_pilot_program`  
**Event Category:** `conversion`  
**Trigger Locations:**

| Location | Button Text | Event Label | File |
|----------|-------------|-------------|------|
| Homepage hero pilot spotlight | "Apply for Pilot" | `spotlight_primary` | Index.cshtml:620 |
| Pricing page hero | "Apply for Pilot Program" | `Pricing Hero Apply` | Pricing.cshtml:338 |
| Pricing FAQ CTA | "Apply for Pilot Program" | `FAQ Section Apply` | Pricing.cshtml:767 |
| CoreSuite final CTA | "Apply for Pilot Program" | `Bottom CTA Pilot Application` | CoreSuite.cshtml:1209 |

**Example Implementation:**
```javascript
onclick="gtag('event','apply_pilot_program',{ 
    'button_location':'spotlight_primary',
    'event_category':'conversion' 
});"
```

**Destination:** `/Home/Contact?topic=pilot-application`

---

### 2. Custom Build Inquiry
**Event Name:** `custom_build_inquiry`  
**Event Category:** `conversion`  
**Trigger Locations:**

| Location | Button Text | Event Label | File |
|----------|-------------|-------------|------|
| Homepage hero primary CTA | "Talk about a build" | `company_hero_primary` | Index.cshtml:511 |
| CoreSuite pricing section | "Talk about a custom build" | `Pricing Section Custom Build` | CoreSuite.cshtml:1184 |

**Example Implementation:**
```javascript
onclick="gtag('event','custom_build_inquiry',{ 
    'button_location':'company_hero_primary',
    'event_category':'conversion' 
});"
```

**Destination:** `/Home/Contact?topic=custom-build`

---

### 3. Enterprise Inquiry
**Event Name:** `enterprise_inquiry`  
**Event Category:** `conversion`  
**Trigger Locations:**

| Location | Button Text | Event Label | File |
|----------|-------------|-------------|------|
| Pricing page enterprise tier | "Talk about Enterprise" | `Enterprise Tier Inquiry` | Pricing.cshtml:526 |

**Example Implementation:**
```javascript
onclick="gtag('event', 'enterprise_inquiry', {
    'button_location': 'pricing_tiers',
    'event_category': 'conversion',
    'event_label': 'Enterprise Tier Inquiry'
});"
```

**Destination:** `/Home/Contact?topic=enterprise-multi-site`

---

## 🧭 Navigation Events

### 1. View Core Suite / Emergency Maintenance
**Event Name:** `view_core_suite`  
**Event Category:** `navigation`  
**Trigger Locations:**

| Location | Button Text | Event Label | File |
|----------|-------------|-------------|------|
| Homepage below pilot spotlight | "Explore Emergency Maintenance" | `below_pilot_spotlight` | Index.cshtml:629 |

**Example Implementation:**
```javascript
onclick="gtag('event','view_core_suite',{ 
    'button_location':'below_pilot_spotlight',
    'event_category':'navigation' 
});"
```

**Destination:** `/Home/CoreSuite`

---

### 2. View Pricing
**Event Name:** `view_pricing`  
**Event Category:** `navigation`  
**Trigger Locations:**

| Location | Button Text | Event Label | File |
|----------|-------------|-------------|------|
| CoreSuite pricing preview | "View Tiers & Pricing" | `View Tiers & Pricing` | CoreSuite.cshtml:1174 |
| CoreSuite final CTA | "See Pilot Pricing" | `Bottom CTA Pricing` | CoreSuite.cshtml:1219 |

**Example Implementation:**
```javascript
onclick="gtag('event', 'view_pricing', {
    'button_location': 'pricing_preview',
    'event_category': 'navigation',
    'event_label': 'View Tiers & Pricing'
});"
```

**Destination:** `/Home/Pricing`

---

## 💡 Engagement Events

### 1. Scroll Depth Tracking
**Event Name:** `scroll_depth`  
**Event Category:** (automatic)  
**Implementation:** `Views/Home/CoreSuite.cshtml` (lines 1237-1247)

**Thresholds Tracked:**
- 25% scroll
- 50% scroll
- 75% scroll
- 90% scroll

**Example Implementation:**
```javascript
// Track scroll depth
let scrollTracked = { '25': false, '50': false, '75': false, '90': false };

window.addEventListener('scroll', function () {
    let scrollPercent = (window.scrollY / (document.documentElement.scrollHeight - window.innerHeight)) * 100;

    if (scrollPercent >= 25 && !scrollTracked['25']) { 
        gtag('event', 'scroll_depth', { 'percent': '25' }); 
        scrollTracked['25'] = true; 
    }
    if (scrollPercent >= 50 && !scrollTracked['50']) { 
        gtag('event', 'scroll_depth', { 'percent': '50' }); 
        scrollTracked['50'] = true; 
    }
    if (scrollPercent >= 75 && !scrollTracked['75']) { 
        gtag('event', 'scroll_depth', { 'percent': '75' }); 
        scrollTracked['75'] = true; 
    }
    if (scrollPercent >= 90 && !scrollTracked['90']) { 
        gtag('event', 'scroll_depth', { 'percent': '90' }); 
        scrollTracked['90'] = true; 
    }
});
```

**Note:** Currently only implemented on CoreSuite page. Consider adding to other high-value pages (Homepage, Pricing).

---

### 2. ROI Calculator Interactions
**Event Name:** (not yet tracked)  
**Recommendation:** Add tracking for ROI calculator usage on Pricing page

**Suggested Events:**
```javascript
// When user changes any ROI input
gtag('event', 'roi_calculator_interaction', {
    'event_category': 'engagement',
    'event_label': 'Pricing Page ROI Calculator'
});

// When ROI shows positive savings
gtag('event', 'roi_calculator_positive', {
    'event_category': 'engagement',
    'savings_annual': annualSavings,
    'roi_multiple': roiMultiple
});
```

**Implementation Location:** `Views/Home/Pricing.cshtml` (JavaScript section, ~line 850)

---

## 🔗 UTM Parameter Capture

### Implementation
**Location:** `Views/Home/Contact.cshtml` (lines 170-183)

All contact form submissions automatically capture the following UTM parameters and metadata:

| Parameter | Database Field | Source |
|-----------|---------------|--------|
| `utm_source` | `UtmSource` | Query string |
| `utm_medium` | `UtmMedium` | Query string |
| `utm_campaign` | `UtmCampaign` | Query string |
| `utm_content` | `UtmContent` | Query string |
| `utm_term` | `UtmTerm` | Query string |
| (auto) | `SourcePage` | `window.location.pathname + window.location.search` |
| (auto) | `Referrer` | `document.referrer` |

**Example Implementation:**
```javascript
const params = new URLSearchParams(window.location.search);
const setVal = (name, value) => {
    const el = document.querySelector('[name="' + name + '"]');
    if (el && value) el.value = value;
};

setVal("UtmSource", params.get("utm_source"));
setVal("UtmMedium", params.get("utm_medium"));
setVal("UtmCampaign", params.get("utm_campaign"));
setVal("UtmContent", params.get("utm_content"));
setVal("UtmTerm", params.get("utm_term"));
setVal("SourcePage", window.location.pathname + window.location.search);
setVal("Referrer", document.referrer || "");
```

**Database Storage:** All UTM parameters and metadata are stored in the `WebLeads` table for attribution analysis.

---

## 🎥 Microsoft Clarity Setup

### Implementation
**Location:** `Views/Shared/_Layout.cshtml` (lines 50-57)  
**Project ID:** `ukl6dgxpfg`

```javascript
<!-- Microsoft Clarity -->
<script>
    (function (c, l, a, r, i, t, y) {
        c[a] = c[a] || function () { (c[a].q = c[a].q || []).push(arguments) };
        t = l.createElement(r); t.async = 1; t.src = "https://www.clarity.ms/tag/" + i;
        y = l.getElementsByTagName(r)[0]; y.parentNode.insertBefore(t, y);
    })(window, document, "clarity", "script", "ukl6dgxpfg");
</script>
```

### What Clarity Tracks
- Session recordings (anonymized)
- Heatmaps (click, scroll, area)
- Rage clicks
- Dead clicks
- Excessive scrolling
- Quick backs
- JavaScript errors
- Conversion funnels (custom)

### Recommended Clarity Dashboards
1. **Pilot Application Funnel:**
   - Homepage → Pricing → Contact (pilot-application)
   - Homepage spotlight → Contact (pilot-application)

2. **Pricing Engagement:**
   - Entry point → Tier comparison → ROI calculator → CTA click

3. **Error Monitoring:**
   - Form validation errors
   - Rage clicks on disabled buttons
   - Dead clicks on non-interactive elements

---

## 📊 Event Categories & Labels

### Event Categories (Standardized)
| Category | Usage | Examples |
|----------|-------|----------|
| `conversion` | Primary conversion actions | `apply_pilot_program`, `custom_build_inquiry`, `enterprise_inquiry` |
| `navigation` | Internal page navigation | `view_core_suite`, `view_pricing` |
| `engagement` | Content interaction | `scroll_depth`, `roi_calculator_interaction` |
| `form` | Form interactions | `form_start`, `form_error`, `form_submit` (recommended to add) |

### Button Locations (Standardized)
| Location Value | Description | Pages |
|----------------|-------------|-------|
| `company_hero_primary` | Primary CTA in homepage hero | Index.cshtml |
| `spotlight_primary` | Pilot program spotlight band | Index.cshtml |
| `below_pilot_spotlight` | CTA below pilot spotlight | Index.cshtml |
| `pricing_hero` | Hero section on pricing page | Pricing.cshtml |
| `pricing_tiers` | Tier card CTAs on pricing | Pricing.cshtml |
| `pricing_faq_cta` | FAQ section CTA on pricing | Pricing.cshtml |
| `pricing_preview` | Pricing preview on CoreSuite | CoreSuite.cshtml |
| `final_cta_band` | Bottom page CTA band | CoreSuite.cshtml |

---

## 🎯 Recommended GA4 Conversions

### Setup in GA4 Admin → Events → Mark as Conversion

1. **Primary Conversions (Critical):**
   - ✅ `apply_pilot_program` — **CRITICAL** pilot partner recruitment
   - ✅ `custom_build_inquiry` — High-value custom projects
   - ✅ `enterprise_inquiry` — Enterprise tier interest

2. **Secondary Conversions (Important):**
   - ⚠️ `form_submit` — Any contact form submission (recommended to add)
   - ⚠️ `launch_list_signup` — Email capture for launch notifications (recommended to add)

3. **Micro-Conversions (Engagement):**
   - 📊 `view_pricing` — Pricing page views (indicates interest)
   - 📊 `roi_calculator_positive` — ROI calculator shows positive results (recommended to add)
   - 📊 `scroll_depth` (90%) — Deep engagement with content

### Conversion Value Assignment (Optional)
Assign estimated values to conversions for ROI analysis:

| Conversion | Estimated Value | Reasoning |
|------------|----------------|-----------|
| `apply_pilot_program` | $5,000 | 50% chance × $10k pilot value |
| `custom_build_inquiry` | $2,000 | Qualified lead for custom project |
| `enterprise_inquiry` | $10,000 | High-ticket enterprise opportunity |
| `launch_list_signup` | $50 | Early interest, future conversion potential |

---

## ✅ Testing Checklist

### Pre-Production Testing
- [ ] **GA4 Real-Time Report:** Verify events appear in GA4 real-time view
- [ ] **Event Parameters:** Check that `button_location`, `event_category`, and `event_label` are captured
- [ ] **UTM Parameters:** Test contact form with UTM parameters in URL
- [ ] **Clarity Recordings:** Verify session recordings are capturing
- [ ] **Clarity Heatmaps:** Check click and scroll heatmaps are generating
- [ ] **Cross-Browser:** Test on Chrome, Firefox, Safari, Edge
- [ ] **Mobile Testing:** Verify tracking on mobile devices (iOS, Android)
- [ ] **Ad Blockers:** Test with common ad blockers (uBlock Origin, AdBlock Plus)

### Post-Production Verification (First 24-48 Hours)
- [ ] **Event Volume:** Verify reasonable event counts in GA4
- [ ] **Conversion Paths:** Check User Acquisition → Engagement → Conversions
- [ ] **Clarity Insights:** Review rage clicks, dead clicks, and error reports
- [ ] **Form Submissions:** Verify WebLeads table entries match GA4 events
- [ ] **Email Notifications:** Confirm emails arrive at michael@sentinelaisystems.net
- [ ] **UTM Attribution:** Check if UTM parameters are being stored in database
- [ ] **Scroll Depth:** Verify scroll depth events are firing (CoreSuite page)

### Weekly Monitoring (Ongoing)
- [ ] **Conversion Rates:** Track week-over-week conversion rate trends
- [ ] **Button Location Performance:** Compare CTR across button locations
- [ ] **Traffic Sources:** Analyze top referrers and landing pages
- [ ] **Clarity Insights:** Review weekly automatic insights and anomalies
- [ ] **Form Abandonment:** Identify drop-off points in contact forms

---

## 🔮 Future Enhancements (Recommended)

### High Priority
1. **Add Form Interaction Tracking:**
   ```javascript
   // Form start event (when first field is focused)
   gtag('event', 'form_start', {
       'event_category': 'form',
       'form_name': 'contact',
       'form_topic': topic
   });

   // Form submission event
   gtag('event', 'form_submit', {
       'event_category': 'form',
       'form_name': 'contact',
       'form_topic': topic
   });
   ```

2. **Add Launch List Signup Tracking:**
   ```javascript
   gtag('event', 'launch_list_signup', {
       'event_category': 'conversion',
       'signup_location': 'launch_list_page'
   });
   ```

3. **Add ROI Calculator Interaction Tracking:**
   - Track when users interact with calculator
   - Track when results show positive ROI
   - Track tier selection changes

4. **Expand Scroll Depth Tracking:**
   - Add to Homepage (Index.cshtml)
   - Add to Pricing page (Pricing.cshtml)
   - Add to Platform page (Platform.cshtml)

### Medium Priority
5. **Add FAQ Interaction Tracking:**
   ```javascript
   // Track which FAQ questions are most viewed
   gtag('event', 'faq_expand', {
       'event_category': 'engagement',
       'faq_question': questionId
   });
   ```

6. **Add Navigation Menu Tracking:**
   ```javascript
   // Track dropdown menu usage
   gtag('event', 'nav_dropdown', {
       'event_category': 'navigation',
       'dropdown_name': 'solutions'
   });
   ```

7. **Add Video Tracking (if videos added):**
   ```javascript
   gtag('event', 'video_progress', {
       'event_category': 'engagement',
       'video_name': 'em_module_demo',
       'video_percent': 25
   });
   ```

### Low Priority (Nice to Have)
8. **Add Pricing Tier Comparison Tracking:**
   - Track when users toggle between monthly/annual billing
   - Track tier card hover interactions (Clarity shows this, but GA4 event would help)

9. **Add Exit Intent Tracking:**
   ```javascript
   // Track exit intent popups (if implemented)
   gtag('event', 'exit_intent_shown', {
       'event_category': 'engagement'
   });
   ```

10. **Add Chat Widget Tracking (if chat added):**
    ```javascript
    gtag('event', 'chat_opened', {
        'event_category': 'engagement',
        'chat_location': pageLocation
    });
    ```

---

## 📈 Key Metrics to Monitor

### Conversion Metrics
- **Pilot Application Rate:** (apply_pilot_program events / total sessions) × 100
- **Custom Build Inquiry Rate:** (custom_build_inquiry events / total sessions) × 100
- **Pricing Page Conversion Rate:** (conversions from /Home/Pricing / pricing page views) × 100

### Engagement Metrics
- **Avg. Session Duration:** Target > 2 minutes
- **Pages per Session:** Target > 3 pages
- **Bounce Rate:** Target < 40%
- **Scroll Depth (90%):** Target > 20% of sessions on CoreSuite

### Traffic Quality Metrics
- **Direct Traffic:** Brand awareness indicator
- **Organic Search:** SEO effectiveness
- **Referral Traffic:** Partnership and outreach effectiveness
- **UTM Campaign Performance:** Paid/earned media attribution

---

## 📞 Support & Resources

- **GA4 Property:** [Google Analytics Dashboard](https://analytics.google.com/analytics/web/#/p467878499/)
- **Clarity Dashboard:** [Microsoft Clarity](https://clarity.microsoft.com/projects/view/ukl6dgxpfg/)
- **WebLeads Database:** Check `Data/ApplicationDbContext.cs` → `WebLeads` table
- **Email Notifications:** michael@sentinelaisystems.net
- **SMTP Service:** `Services/SmtpEmailSender.cs`

---

**Document Maintained By:** Michael Henley  
**Next Review Date:** Q1 2026 (after pilot partner program launch)

