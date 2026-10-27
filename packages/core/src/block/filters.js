/**
 * Built-in ad/tracker rules shipped with agbrowser.
 *
 * Kept deliberately small and hand-tuned: the agent runtime blocks ads at the
 * network layer already, so this list only needs to cover what the built-in
 * host-resolver rules cannot (path-level and third-party patterns).
 */
export const DEFAULT_FILTERS = `
! title: agbrowser default filters
! description: built-in network + cosmetic ad rules

||adservice.google.com^
||pagead2.googlesyndication.com^
||googleads.g.doubleclick.net^
||adsystem.com^
||2mdn.net^
||adnxs.com^
||adsrvr.org^
||amazon-adsystem.com^
||criteo.com^
||criteo.net^
||pubmatic.com^
||rubiconproject.com^
||openx.net^
||taboola.com^
||outbrain.com^
||sharethrough.com^
||zedo.com^
||adroll.com^
||branch.io^
||app-measurement.com^
||firebaseinstallations.googleapis.com^
||analytics.google.com^
||analytics.yahoo.com^
||scorecardresearch.com^
||quantserve.com^
||quantcount.com^
||hotjar.com^
||fullstory.com^
||mouseflow.com^
||mixpanel.com^
||segment.io^
||segment.com^
||amplitude.com^
||heap.io^
||clarity.ms^
||intercomcdn.com^
||sentry.io^
||bugsnag.com^
||doubleclick.net^
||googletagservices.com^
||adservice.googleusercontent.com^
||googleadservices.com^
||yieldmo.com^
||smartadserver.com^
||casalemedia.com^
|| casalemedia.com^
||33across.com^
||adsafeprotected.com^
||permutive.com^
||prebid.org^
||prebid-server.com^
||confiant.com^
||gumgum.com^
||yieldlab.net^
||teads.tv^
||smartadserver.com^

! generic ad containers
##.ad-container
##.ad-wrapper
##.adsbygoogle
##.adsbygoogle
##.ad-banner
##.ad-placement
##.ad-slot
##.advertisement
##.sponsored-content
##[id^="google_ads_"]
##[id^="div-gpt-ad"]
##[id^="banner-ad"]
##.taboola-container
##.outbrain-widget
##.criteo-ad
##.ad-container-wrapper
##.adsbygoogle-wrap

! social widgets that slow pages down without being useful to agents
##.facebook-like
##.twitter-share-button
##.linkedin-share-button
##.social-share-bar
`.trim();
