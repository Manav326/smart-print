package com.quantaedge.smartprint

import android.content.Context

/**
 * Customer-visible Android copy only. Internal identifiers, backend values,
 * diagnostics and document contents are intentionally never translated.
 */
internal object AndroidCustomerLocale {
    private const val PREFS = "smartprint.android.customer-ui"
    private const val KEY = "locale"

    const val ENGLISH = "english"
    const val HINDI = "hindi"
    const val HINGLISH = "hinglish"

    fun current(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY, ENGLISH)
            ?.takeIf { it in setOf(ENGLISH, HINDI, HINGLISH) } ?: ENGLISH

    fun save(context: Context, locale: String) {
        if (locale in setOf(ENGLISH, HINDI, HINGLISH)) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(KEY, locale).apply()
        }
    }

    fun label(context: Context): String = when (current(context)) {
        HINDI -> "भाषा · हिन्दी"
        HINGLISH -> "Language · Hinglish"
        else -> "Language · English"
    }

    fun translate(context: Context, source: String): String =
        translate(current(context), source)

    /** Pure translation lookup, also used by JVM regression tests. */
    fun translate(locale: String, source: String): String {
        if (locale == ENGLISH) return source
        val exact = when (locale) {
            HINDI -> hindiCopy[source]
            HINGLISH -> hinglishCopy[source]
            else -> null
        }
        if (exact != null) return exact

        // Preserve customer data and interpolated values while translating
        // the surrounding UI labels and count/status wording.
        return when (locale) {
            HINDI -> when {
                source == "PRIVATE" -> "निजी"
                source.startsWith("SPONSORED · ") -> "प्रायोजित · " + source.removePrefix("SPONSORED · ")
                source.startsWith("Folder  ·  ") -> "फ़ोल्डर  ·  " + source.removePrefix("Folder  ·  ")
                source.startsWith("Folder · ") -> "फ़ोल्डर · " + source.removePrefix("Folder · ")
                source.startsWith("Page ") -> "पेज " + source.removePrefix("Page ")
                source.endsWith(" folders selected") -> source.removeSuffix(" folders selected") + " फ़ोल्डर चुने गए"
                source.endsWith(" selected") -> source.removeSuffix(" selected") + " चुने गए"
                source.endsWith(" orders") -> source.removeSuffix(" orders") + " ऑर्डर"
                source.endsWith(" used on this device") -> source.removeSuffix(" used on this device") + " डिवाइस पर इस्तेमाल"
                source.startsWith("Photo layout · ") -> "फोटो लेआउट · " + source.removePrefix("Photo layout · ")
                else -> source
            }
            HINGLISH -> when {
                source == "PRIVATE" -> "Private"
                source.startsWith("SPONSORED · ") -> "Sponsored · " + source.removePrefix("SPONSORED · ")
                source.startsWith("Folder  ·  ") -> "Folder  ·  " + source.removePrefix("Folder  ·  ")
                source.startsWith("Folder · ") -> "Folder · " + source.removePrefix("Folder · ")
                source.startsWith("Page ") -> "Page " + source.removePrefix("Page ")
                source.endsWith(" folders selected") -> source.removeSuffix(" folders selected") + " folders selected"
                source.endsWith(" selected") -> source.removeSuffix(" selected") + " selected"
                source.endsWith(" orders") -> source.removeSuffix(" orders") + " orders"
                source.endsWith(" used on this device") -> source.removeSuffix(" used on this device") + " device par used"
                source.startsWith("Photo layout · ") -> "Photo layout · " + source.removePrefix("Photo layout · ")
                else -> source
            }
            else -> source
        }
    }

    /**
     * The embedded customer/shop journey uses the same locale key as the
     * portal LanguageProvider. Install before first paint on the trusted origin.
     */
    fun documentStartScript(context: Context): String =
        documentStartScript(current(context))

    fun documentStartScript(localeValue: String): String {
        val locale = localeValue.takeIf { it in setOf(ENGLISH, HINDI, HINGLISH) } ?: ENGLISH
        return """(function(){try{var l='$locale';localStorage.setItem('smartprint-ui-locale',l);document.cookie='smartprint-ui-locale='+l+'; Path=/; Max-Age=31536000; SameSite=Lax'+(location.protocol==='https:'?'; Secure':'');window.dispatchEvent(new StorageEvent('storage',{key:'smartprint-ui-locale',newValue:l}));}catch(e){}})();"""
    }

    private val hindiCopy = mapOf(
        "Language" to "भाषा", "SMARTPRINT  ·  YOUR PRINT SPACE" to "स्मार्टप्रिंट  ·  आपकी प्रिंटिंग की जगह", "Choose the language for customer-facing app screens." to "ग्राहक को दिखने वाली ऐप स्क्रीन की भाषा चुनें।",
        "My Files" to "मेरी फ़ाइलें", "Print at a shop" to "शॉप पर प्रिंट करें",
        "Home" to "होम", "Files" to "फ़ाइलें", "Print" to "प्रिंट", "Activity" to "गतिविधि",
        "Account" to "अकाउंट", "Back" to "वापस", "Back to Home" to "होम पर जाएँ",
        "Appearance" to "दिखावट", "Choose how SmartPrint looks on this device. This setting is kept when you sign out." to "इस डिवाइस पर SmartPrint की दिखावट चुनें। साइन आउट करने पर भी यह सेटिंग बनी रहेगी।",
        "Light theme" to "लाइट थीम", "Dark theme" to "डार्क थीम",
        "Sign in" to "साइन इन", "Sign out" to "साइन आउट", "Sign out?" to "साइन आउट करें?",
        "Create account" to "अकाउंट बनाएँ", "Name" to "नाम", "Email address" to "ईमेल पता",
        "Password" to "पासवर्ड", "Password · 12+ chars" to "पासवर्ड · कम से कम 12 अक्षर",
        "Complete the required fields." to "कृपया ज़रूरी जानकारी भरें।",
        "Print history" to "प्रिंट हिस्ट्री", "Local files stay on this phone." to "लोकल फ़ाइलें इसी फ़ोन में रहेंगी।",
        "Guest access is ready" to "गेस्ट एक्सेस तैयार है", "Smart Print customer" to "Smart Print ग्राहक",
        "Your next print starts here." to "आपकी अगली प्रिंटिंग यहीं से शुरू होती है।",
        "Everything ready for your next print." to "आपकी अगली प्रिंटिंग के लिए सब तैयार है।",
        "Your files stay private. Print when ready." to "आपकी फ़ाइलें निजी रहती हैं। तैयार होने पर प्रिंट करें।",
        "Find a print shop" to "प्रिंट शॉप खोजें", "Scan the shop QR to connect" to "जुड़ने के लिए शॉप QR स्कैन करें",
        "Connected · tap to manage" to "कनेक्टेड · मैनेज करने के लिए टैप करें",
        "Manage connected shop" to "कनेक्टेड शॉप मैनेज करें",
        "ID Studio" to "ID स्टूडियो", "Photo Studio" to "फोटो स्टूडियो",
        "Your space" to "आपकी जगह", "Recent files" to "हाल की फ़ाइलें", "View library  ›" to "लाइब्रेरी देखें  ›",
        "Scan document" to "दस्तावेज़ स्कैन करें", "Choose files" to "फ़ाइलें चुनें",
        "Your files will appear here" to "आपकी फ़ाइलें यहाँ दिखेंगी",
        "Scan a document or choose files from your phone." to "दस्तावेज़ स्कैन करें या फ़ोन से फ़ाइलें चुनें।",
        "Connect to a shop" to "शॉप से जुड़ें", "Scan the Shop QR to send documents to this shop." to "इस शॉप पर दस्तावेज़ भेजने के लिए शॉप QR स्कैन करें।",
        "Scan Shop QR" to "शॉप QR स्कैन करें", "Check Wi-Fi" to "वाई-फ़ाई जाँचें", "Open My Files" to "मेरी फ़ाइलें खोलें",
        "Already on the shop Wi-Fi? Check that your phone can reach the shop." to "क्या आप शॉप के वाई-फ़ाई पर हैं? जाँचें कि आपका फ़ोन शॉप से जुड़ पा रहा है।",
        "Cancel" to "रद्द करें", "Close" to "बंद करें", "Try again" to "फिर कोशिश करें",
        "Opening Photo Studio…" to "फोटो स्टूडियो खुल रहा है…", "Photo Studio couldn’t open" to "फोटो स्टूडियो नहीं खुल सका",
        "Export" to "एक्सपोर्ट", "Import" to "इम्पोर्ट", "Delete" to "डिलीट", "Rename" to "नाम बदलें",
        "Select all" to "सभी चुनें", "Search files" to "फ़ाइलें खोजें", "No files found" to "कोई फ़ाइल नहीं मिली",
        "Save" to "सेव करें", "Continue" to "जारी रखें", "Done" to "हो गया",
        "Camera" to "कैमरा", "Gallery" to "गैलरी", "Documents" to "दस्तावेज़",
        "Connect to shop Wi-Fi" to "शॉप के वाई-फ़ाई से जुड़ें",
        "Payment QR saved to your device." to "पेमेंट QR आपके डिवाइस में सेव हो गया।",
        "Could not share this document." to "यह दस्तावेज़ शेयर नहीं हो सका।",
        "No app is available to open this link." to "इस लिंक को खोलने के लिए कोई ऐप उपलब्ध नहीं है।",
        "Add folders" to "फ़ोल्डर जोड़ें", "Add photos of your ID" to "अपने ID की फ़ोटो जोड़ें",
        "Browse phone" to "फ़ोन ब्राउज़ करें", "Cancel scan" to "स्कैन रद्द करें",
        "Choose from SmartPrint My Files" to "SmartPrint मेरी फ़ाइलों से चुनें",
        "Combine PDF" to "PDF जोड़ें", "Continue editing" to "एडिटिंग जारी रखें",
        "Continue with Google" to "Google से जारी रखें", "Create a new ID card" to "नया ID कार्ड बनाएँ",
        "Create a new folder" to "नया फ़ोल्डर बनाएँ", "Edit" to "एडिट करें",
        "Existing folders" to "मौजूदा फ़ोल्डर", "Files already saved in SmartPrint" to "SmartPrint में पहले से सेव फ़ाइलें",
        "Files in this order" to "इस ऑर्डर की फ़ाइलें", "Front" to "आगे", "Go" to "जाएँ",
        "ID Studio · Private documents" to "ID स्टूडियो · निजी दस्तावेज़", "ID card" to "ID कार्ड",
        "Import files" to "फ़ाइलें इम्पोर्ट करें", "Import folder" to "फ़ोल्डर इम्पोर्ट करें",
        "Import from phone gallery instead" to "फ़ोन गैलरी से इम्पोर्ट करें", "Import photo" to "फ़ोटो इम्पोर्ट करें",
        "Move" to "मूव करें", "No orders match this status." to "इस स्टेटस से कोई ऑर्डर नहीं मिला।",
        "No saved folders yet. You can create one above." to "अभी कोई फ़ोल्डर सेव नहीं है। ऊपर नया फ़ोल्डर बनाएँ।",
        "Order progress" to "ऑर्डर की स्थिति", "Payment summary" to "पेमेंट का सारांश",
        "Photo Studio · Private documents" to "फोटो स्टूडियो · निजी दस्तावेज़", "Prepared ID cards" to "तैयार ID कार्ड",
        "Recent activity" to "हाल की गतिविधि", "Resume" to "फिर शुरू करें", "Retake" to "दोबारा लें",
        "Rotate" to "घुमाएँ", "Rotate back" to "पीछे की ओर घुमाएँ", "Rotate front" to "आगे की ओर घुमाएँ",
        "Save copy" to "कॉपी सेव करें", "Sign in to see your print orders." to "अपने प्रिंट ऑर्डर देखने के लिए साइन इन करें।",
        "SmartPrint ID Studio" to "SmartPrint ID स्टूडियो", "SmartPrint My Files" to "SmartPrint मेरी फ़ाइलें",
        "Status" to "स्टेटस", "Take photo" to "फ़ोटो लें", "Track order" to "ऑर्डर ट्रैक करें",
        "Unfinished ID setup" to "अधूरा ID सेटअप", "Use photo" to "फ़ोटो इस्तेमाल करें", "View" to "देखें",
        "View mode" to "देखने का मोड", "← Up one folder" to "← एक फ़ोल्डर ऊपर",
        "12+ chars · Upper · Lower · Number · Symbol" to "12+ अक्षर · बड़ा · छोटा · नंबर · सिंबल",
        "Connecting securely to Google…" to "Google से सुरक्षित रूप से जुड़ रहे हैं…",
        "Checking for new orders…" to "नए ऑर्डर जाँचे जा रहे हैं…", "Checking this network…" to "इस नेटवर्क की जाँच हो रही है…",
        "Language / भाषा" to "भाषा चुनें",
        "Allow SmartPrint to check the shop computer and send selected documents directly over the shop Wi-Fi. Cloud-assisted upload remains available." to "SmartPrint को शॉप के कंप्यूटर की जाँच करने और चुने हुए दस्तावेज़ सीधे शॉप के वाई-फ़ाई से भेजने दें। क्लाउड के ज़रिए अपलोड का विकल्प भी उपलब्ध रहेगा।",
        "Not now" to "अभी नहीं", "Order details unavailable" to "ऑर्डर की जानकारी उपलब्ध नहीं है",
        "No files were found for this print order." to "इस प्रिंट ऑर्डर के लिए कोई फ़ाइल नहीं मिली।",
        "ID card size · mm" to "ID कार्ड का आकार · mm", "Apply" to "लागू करें",
        "Search this folder" to "इस फ़ोल्डर में खोजें",
        "OR" to "या", "Clear" to "साफ़ करें", "ZIP" to "ZIP",
        "Swipe-free · Use arrows to reorder." to "स्वाइप किए बिना · क्रम बदलने के लिए ऐरो इस्तेमाल करें।",
        "Preparing document…" to "दस्तावेज़ तैयार हो रहा है…", 
        "Private document" to "निजी दस्तावेज़", "Print document" to "प्रिंट दस्तावेज़",
        "Files received" to "फ़ाइलें मिल गईं", "Checking files" to "फ़ाइलों की जाँच हो रही है",
        "Being prepared" to "तैयारी हो रही है", "Approval needed" to "आपकी मंज़ूरी ज़रूरी है",
        "Shop reviewing" to "शॉप जाँच रही है", "Ready for payment" to "पेमेंट के लिए तैयार",
        "Pay at shop" to "शॉप पर पेमेंट करें", "Payment being checked" to "पेमेंट की जाँच हो रही है",
        "Payment not confirmed" to "पेमेंट की पुष्टि नहीं हुई", "Payment received" to "पेमेंट मिल गया",
        "Preparing to print" to "प्रिंट की तैयारी हो रही है", "Printing started" to "प्रिंटिंग शुरू हो गई",
        "Printing" to "प्रिंट हो रहा है", "Shop checking print" to "शॉप प्रिंट की जाँच कर रही है",
        "Preparing PDF" to "PDF तैयार हो रहा है", "PDF ready" to "PDF तैयार है",
        "Ready for collection" to "लेने के लिए तैयार", "Not accepted" to "स्वीकार नहीं किया गया",
        "Refunded" to "रिफंड हो गया", "Partly refunded" to "आंशिक रिफंड हुआ",
        "Cancelled" to "रद्द किया गया", "Could not print" to "प्रिंट नहीं हो सका",
        "Price needs refreshing" to "कीमत अपडेट करनी होगी", "Some files completed" to "कुछ फ़ाइलें पूरी हुईं",
        "Some files need attention" to "कुछ फ़ाइलों पर ध्यान देना ज़रूरी है", "In progress" to "काम जारी है",
        "Order received" to "ऑर्डर मिल गया", "Shop review" to "शॉप की जाँच",
        "Payment" to "पेमेंट", "Order outcome" to "ऑर्डर का नतीजा", "Order closed" to "ऑर्डर बंद",
        "Partial output ready" to "कुछ आउटपुट तैयार", "Output ready" to "आउटपुट तैयार",
        "Your files were submitted to the shop" to "आपकी फ़ाइलें शॉप को भेज दी गई हैं",
        "Your approval is needed before the shop can continue" to "शॉप के आगे बढ़ने के लिए आपकी मंज़ूरी ज़रूरी है",
        "Choose the print options needed to continue" to "आगे बढ़ने के लिए प्रिंट विकल्प चुनें",
        "The shop is reviewing your files" to "शॉप आपकी फ़ाइलों की जाँच कर रही है",
        "The shop is checking your files" to "शॉप आपकी फ़ाइलों की जाँच कर रही है",
        "Waiting for payment" to "पेमेंट का इंतज़ार है",
        "Payment is pending for one or more files" to "एक या अधिक फ़ाइलों का पेमेंट बाकी है",
        "Payment verified" to "पेमेंट सत्यापित है", "Payment refunded" to "पेमेंट रिफंड हो गया",
        "Partial refund recorded" to "आंशिक रिफंड दर्ज हुआ", "Payments refunded" to "पेमेंट रिफंड हो गए",
        "The printer is processing your files" to "प्रिंटर आपकी फ़ाइलों को प्रोसेस कर रहा है",
        "Sending the approved job to the shop computer" to "मंज़ूर किया गया काम शॉप के कंप्यूटर पर भेजा जा रहा है",
        "Waiting for printing to begin" to "प्रिंटिंग शुरू होने का इंतज़ार है",
        "PDF output saved" to "PDF आउटपुट सेव हो गया", "All files completed" to "सभी फ़ाइलें पूरी हुईं",
        "No files produced output" to "किसी फ़ाइल का आउटपुट नहीं बना",
        "The shop is processing your files" to "शॉप आपकी फ़ाइलों को प्रोसेस कर रही है",
        "Review and approval resolved for all files" to "सभी फ़ाइलों की जाँच और मंज़ूरी पूरी हुई",
        "No payment required for excluded files" to "हटाई गई फ़ाइलों के लिए पेमेंट ज़रूरी नहीं",
        "Your print order is complete" to "आपका प्रिंट ऑर्डर पूरा हो गया",
        "Your PDF is ready" to "आपकी PDF तैयार है", "Your order has a partial result" to "आपके ऑर्डर का कुछ हिस्सा पूरा हुआ",
        "Your order was refunded" to "आपके ऑर्डर का रिफंड हो गया", "Your order was cancelled" to "आपका ऑर्डर रद्द हो गया",
        "Your files were not accepted" to "आपकी फ़ाइलें स्वीकार नहीं की गईं",
        "Your print order could not be completed" to "आपका प्रिंट ऑर्डर पूरा नहीं हो सका",
        "Your order has a mixed outcome" to "आपके ऑर्डर के कुछ हिस्सों का नतीजा अलग है",
        "Your order has been received" to "आपका ऑर्डर मिल गया",
        "Order total" to "ऑर्डर का कुल", "Paid" to "भुगतान किया गया",
        "Both sides are ready. Review them before preparing your A4 sheet." to "दोनों तरफ़ की फ़ोटो तैयार हैं। A4 शीट बनाने से पहले उन्हें जाँच लें।",
        "Front photo saved. Add the back of your ID to continue." to "आगे की फ़ोटो सेव हो गई। जारी रखने के लिए ID का पीछे वाला हिस्सा जोड़ें।",
        "Back photo saved. Add the front of your ID to continue." to "पीछे की फ़ोटो सेव हो गई। जारी रखने के लिए ID का आगे वाला हिस्सा जोड़ें।",
        "Add the front and back. You can take a new photo or choose one you already have." to "आगे और पीछे की फ़ोटो जोड़ें। नई फ़ोटो लें या पहले से मौजूद फ़ोटो चुनें।",
        "FRONT + BACK" to "आगे + पीछे"
    )

    private val hinglishCopy = mapOf(
        "Language" to "Language", "SMARTPRINT  ·  YOUR PRINT SPACE" to "SMARTPRINT  ·  Aapki print space", "Choose the language for customer-facing app screens." to "Customer app screens ki language choose karein.",
        "My Files" to "Meri Files", "Print at a shop" to "Shop par print karein",
        "Home" to "Home", "Files" to "Files", "Print" to "Print", "Activity" to "Activity",
        "Account" to "Account", "Back" to "Back", "Back to Home" to "Home par jaayein",
        "Appearance" to "Look & Feel", "Choose how SmartPrint looks on this device. This setting is kept when you sign out." to "Is device par SmartPrint ka look choose karein. Sign out ke baad bhi setting save rahegi.",
        "Light theme" to "Light theme", "Dark theme" to "Dark theme",
        "Sign in" to "Sign in", "Sign out" to "Sign out", "Sign out?" to "Sign out karein?",
        "Create account" to "Account banayein", "Name" to "Naam", "Email address" to "Email address",
        "Password" to "Password", "Password · 12+ chars" to "Password · 12+ chars",
        "Complete the required fields." to "Please zaroori details fill karein.",
        "Print history" to "Print history", "Local files stay on this phone." to "Aapki local files isi phone mein rahengi.",
        "Guest access is ready" to "Guest access ready hai", "Smart Print customer" to "Smart Print customer",
        "Your next print starts here." to "Aapki next print yahin se start hoti hai.",
        "Everything ready for your next print." to "Aapki next print ke liye sab ready hai.",
        "Your files stay private. Print when ready." to "Aapki files private rahengi. Ready hone par print karein.",
        "Find a print shop" to "Print shop dhoondhein", "Scan the shop QR to connect" to "Connect karne ke liye shop QR scan karein",
        "Connected · tap to manage" to "Connected · manage karne ke liye tap karein",
        "ID Studio" to "ID Studio", "Photo Studio" to "Photo Studio",
        "Your space" to "Aapki space", "Recent files" to "Recent files", "View library  ›" to "Library dekhein  ›",
        "Scan document" to "Document scan karein", "Choose files" to "Files choose karein",
        "Your files will appear here" to "Aapki files yahan dikhenge",
        "Scan a document or choose files from your phone." to "Document scan karein ya phone se files choose karein.",
        "Connect to a shop" to "Shop se connect karein", "Scan the Shop QR to send documents to this shop." to "Is shop par documents bhejne ke liye Shop QR scan karein.",
        "Scan Shop QR" to "Shop QR scan karein", "Check Wi-Fi" to "Wi-Fi check karein", "Open My Files" to "My Files kholein",
        "Already on the shop Wi-Fi? Check that your phone can reach the shop." to "Shop Wi-Fi par hain? Check karein ki phone shop se connect ho raha hai.",
        "Cancel" to "Cancel", "Close" to "Close", "Try again" to "Dobara try karein",
        "Opening Photo Studio…" to "Photo Studio open ho raha hai…", "Photo Studio couldn’t open" to "Photo Studio open nahi ho saka",
        "Export" to "Export", "Import" to "Import", "Delete" to "Delete", "Rename" to "Rename",
        "Select all" to "Sab select karein", "Search files" to "Files search karein", "No files found" to "Koi file nahi mili",
        "Save" to "Save karein", "Continue" to "Continue", "Done" to "Done",
        "Camera" to "Camera", "Gallery" to "Gallery", "Documents" to "Documents",
        "Connect to shop Wi-Fi" to "Shop Wi-Fi se connect karein",
        "Payment QR saved to your device." to "Payment QR aapke device mein save ho gaya.",
        "Could not share this document." to "Ye document share nahi ho saka.",
        "No app is available to open this link." to "Is link ko open karne ke liye app available nahi hai."
        ,
        "Add folders" to "Folders add karein", "Add photos of your ID" to "Apne ID ki photos add karein",
        "Browse phone" to "Phone browse karein", "Cancel scan" to "Scan cancel karein",
        "Choose from SmartPrint My Files" to "SmartPrint My Files se choose karein",
        "Combine PDF" to "PDF combine karein", "Continue editing" to "Editing continue karein",
        "Continue with Google" to "Google ke saath continue karein", "Create a new ID card" to "Naya ID card banayein",
        "Create a new folder" to "Naya folder banayein", "Edit" to "Edit karein",
        "Existing folders" to "Existing folders", "Files already saved in SmartPrint" to "SmartPrint mein pehle se saved files",
        "Files in this order" to "Is order ki files", "Front" to "Front", "Go" to "Jaayein",
        "ID Studio · Private documents" to "ID Studio · Private documents", "ID card" to "ID card",
        "Import files" to "Files import karein", "Import folder" to "Folder import karein",
        "Import from phone gallery instead" to "Phone gallery se import karein", "Import photo" to "Photo import karein",
        "Move" to "Move karein", "No orders match this status." to "Is status ke liye koi order nahi mila.",
        "No saved folders yet. You can create one above." to "Abhi koi folder saved nahi hai. Upar naya folder bana sakte hain.",
        "Order progress" to "Order progress", "Payment summary" to "Payment summary",
        "Photo Studio · Private documents" to "Photo Studio · Private documents", "Prepared ID cards" to "Prepared ID cards",
        "Recent activity" to "Recent activity", "Resume" to "Resume karein", "Retake" to "Dobara lein",
        "Rotate" to "Rotate karein", "Rotate back" to "Back side rotate karein", "Rotate front" to "Front side rotate karein",
        "Save copy" to "Copy save karein", "Sign in to see your print orders." to "Print orders dekhne ke liye sign in karein.",
        "SmartPrint ID Studio" to "SmartPrint ID Studio", "SmartPrint My Files" to "SmartPrint My Files",
        "Status" to "Status", "Take photo" to "Photo lein", "Track order" to "Order track karein",
        "Unfinished ID setup" to "Adhura ID setup", "Use photo" to "Photo use karein", "View" to "Dekhein",
        "View mode" to "View mode", "← Up one folder" to "← Ek folder upar",
        "12+ chars · Upper · Lower · Number · Symbol" to "12+ chars · Upper · Lower · Number · Symbol",
        "Connecting securely to Google…" to "Google se securely connect ho raha hai…",
        "Checking for new orders…" to "New orders check ho rahe hain…", "Checking this network…" to "Network check ho raha hai…",
        "Language / भाषा" to "Language choose karein",
        "Allow SmartPrint to check the shop computer and send selected documents directly over the shop Wi-Fi. Cloud-assisted upload remains available." to "SmartPrint ko shop computer check karne aur selected documents directly shop Wi-Fi se bhejne dein. Cloud-assisted upload bhi available rahega.",
        "Not now" to "Abhi nahi", "Order details unavailable" to "Order details available nahi hain",
        "No files were found for this print order." to "Is print order ke liye koi file nahi mili.",
        "ID card size · mm" to "ID card size · mm", "Apply" to "Apply karein",
        "Search this folder" to "Is folder mein search karein",
        "OR" to "YA", "Clear" to "Clear", "ZIP" to "ZIP",
        "Swipe-free · Use arrows to reorder." to "Swipe ki zaroorat nahi · Order badalne ke liye arrows use karein.",
        "Preparing document…" to "Document prepare ho raha hai…", 
        "Private document" to "Private document", "Print document" to "Print document",
        "Files received" to "Files receive ho gayi", "Checking files" to "Files check ho rahi hain",
        "Being prepared" to "Preparation ho rahi hai", "Approval needed" to "Aapki approval zaroori hai",
        "Shop reviewing" to "Shop review kar rahi hai", "Ready for payment" to "Payment ke liye ready",
        "Pay at shop" to "Shop par payment karein", "Payment being checked" to "Payment verify ho raha hai",
        "Payment not confirmed" to "Payment confirm nahi hua", "Payment received" to "Payment mil gaya",
        "Preparing to print" to "Print ki preparation ho rahi hai", "Printing started" to "Printing start ho gayi",
        "Printing" to "Print ho raha hai", "Shop checking print" to "Shop print check kar rahi hai",
        "Preparing PDF" to "PDF prepare ho raha hai", "PDF ready" to "PDF ready hai",
        "Ready for collection" to "Collect karne ke liye ready", "Not accepted" to "Accept nahi hua",
        "Refunded" to "Refund ho gaya", "Partly refunded" to "Partial refund hua",
        "Cancelled" to "Cancel ho gaya", "Could not print" to "Print nahi ho saka",
        "Price needs refreshing" to "Price refresh karni hogi", "Some files completed" to "Kuch files complete ho gayi",
        "Some files need attention" to "Kuch files par attention zaroori hai", "In progress" to "Kaam chal raha hai",
        "Order received" to "Order receive ho gaya", "Shop review" to "Shop review",
        "Payment" to "Payment", "Order outcome" to "Order ka result", "Order closed" to "Order close ho gaya",
        "Partial output ready" to "Partial output ready", "Output ready" to "Output ready",
        "Your files were submitted to the shop" to "Aapki files shop ko submit ho gayi hain",
        "Your approval is needed before the shop can continue" to "Shop ke continue karne se pehle aapki approval zaroori hai",
        "Choose the print options needed to continue" to "Continue karne ke liye print options choose karein",
        "The shop is reviewing your files" to "Shop aapki files review kar rahi hai",
        "The shop is checking your files" to "Shop aapki files check kar rahi hai",
        "Waiting for payment" to "Payment ka wait hai",
        "Payment is pending for one or more files" to "Ek ya zyada files ka payment pending hai",
        "Payment verified" to "Payment verify ho gaya", "Payment refunded" to "Payment refund ho gaya",
        "Partial refund recorded" to "Partial refund record ho gaya", "Payments refunded" to "Payments refund ho gaye",
        "The printer is processing your files" to "Printer aapki files process kar raha hai",
        "Sending the approved job to the shop computer" to "Approved job shop computer par bheja ja raha hai",
        "Waiting for printing to begin" to "Printing start hone ka wait hai",
        "PDF output saved" to "PDF output save ho gaya", "All files completed" to "Saari files complete ho gayi",
        "No files produced output" to "Kisi file ka output nahi bana",
        "The shop is processing your files" to "Shop aapki files process kar rahi hai",
        "Review and approval resolved for all files" to "Saari files ka review aur approval complete hai",
        "No payment required for excluded files" to "Excluded files ke liye payment zaroori nahi",
        "Your print order is complete" to "Aapka print order complete ho gaya",
        "Your PDF is ready" to "Aapki PDF ready hai", "Your order has a partial result" to "Aapke order ka kuch hissa complete hua",
        "Your order was refunded" to "Aapke order ka refund ho gaya", "Your order was cancelled" to "Aapka order cancel ho gaya",
        "Your files were not accepted" to "Aapki files accept nahi hui",
        "Your print order could not be completed" to "Aapka print order complete nahi ho saka",
        "Your order has a mixed outcome" to "Aapke order ke kuch results alag hain",
        "Your order has been received" to "Aapka order receive ho gaya",
        "Order total" to "Order total", "Paid" to "Paid",
        "Both sides are ready. Review them before preparing your A4 sheet." to "Dono sides ki photos ready hain. A4 sheet banane se pehle review karein.",
        "Front photo saved. Add the back of your ID to continue." to "Front photo save ho gayi. Continue karne ke liye ID ka back add karein.",
        "Back photo saved. Add the front of your ID to continue." to "Back photo save ho gayi. Continue karne ke liye ID ka front add karein.",
        "Add the front and back. You can take a new photo or choose one you already have." to "Front aur back photos add karein. Nayi photo lein ya pehle se saved photo choose karein.",
        "FRONT + BACK" to "FRONT + BACK"
    )
}
