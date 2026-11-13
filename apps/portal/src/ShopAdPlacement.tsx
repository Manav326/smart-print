import { useEffect, useState } from "react";
import { Megaphone } from "lucide-react";
import { getPublicAdCampaign, recordPublicAdEvent, type PublicAdCampaign } from "./api";
export default function ShopAdPlacement({placement}:{placement:"SHOP_OVERVIEW"}) {
 const [campaign,setCampaign]=useState<PublicAdCampaign|null>(null);
 useEffect(()=>{let active=true;void getPublicAdCampaign(placement).then(value=>{if(!active||!value)return;setCampaign(value);void recordPublicAdEvent(value.id,"IMPRESSION");}).catch(()=>undefined);return()=>{active=false;};},[placement]);
 if(!campaign)return null;
 return <div className="shop-ad-slot"><a className="shop-ad-banner" href={campaign.destinationUrl} target="_blank" rel="noopener noreferrer" onClick={()=>void recordPublicAdEvent(campaign.id,"CLICK")} aria-label={"Sponsored: "+campaign.headline}><img src={campaign.imageUrl} alt="" loading="lazy"/><div className="shop-ad-copy"><span className="shop-ad-label"><Megaphone size={11}/> Sponsored · {campaign.advertiserName}</span><strong>{campaign.headline}</strong><p>{campaign.body}</p></div><span className="shop-ad-arrow" aria-hidden="true">↗</span></a></div>;
}
