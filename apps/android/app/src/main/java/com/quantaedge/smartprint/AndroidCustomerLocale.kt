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
        "Language" to "भाषा", "Choose the language for customer-facing app screens." to "ग्राहक को दिखने वाली ऐप स्क्रीन की भाषा चुनें।",
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
        "No app is available to open this link." to "इस लिंक को खोलने के लिए कोई ऐप उपलब्ध नहीं है।"
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
        "OR" to "या", "Clear" to "साफ़ करें", "ZIP" to "ZIP",
        "Swipe-free · Use arrows to reorder." to "स्वाइप किए बिना · क्रम बदलने के लिए ऐरो इस्तेमाल करें।",
        "Preparing document…" to "दस्तावेज़ तैयार हो रहा है…", "FRONT + BACK" to "आगे + पीछे"
    )

    private val hinglishCopy = mapOf(
        "Language" to "Language", "Choose the language for customer-facing app screens." to "Customer app screens ki language choose karein.",
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
        "OR" to "YA", "Clear" to "Clear", "ZIP" to "ZIP",
        "Swipe-free · Use arrows to reorder." to "Swipe ki zaroorat nahi · Order badalne ke liye arrows use karein.",
        "Preparing document…" to "Document prepare ho raha hai…", "FRONT + BACK" to "FRONT + BACK"
    )
}
