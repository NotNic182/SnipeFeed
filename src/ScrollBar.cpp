#include "ScrollBar.hpp"
#include "main.hpp"

#include "custom-types/shared/delegate.hpp"

#include "System/Action_1.hpp"
#include "UnityEngine/Camera.hpp"
#include "UnityEngine/Rect.hpp"
#include "UnityEngine/RectTransformUtility.hpp"
#include "UnityEngine/Vector2.hpp"

#include <algorithm>

DEFINE_TYPE(SnipeFeed, FeedScrollBar);

using namespace SnipeFeed;

namespace {
    // Minimum handle height as a fraction of the track, so a very long list
    // still leaves a grabbable handle.
    constexpr float MIN_HANDLE_FRACTION = 0.12f;
}

void FeedScrollBar::Setup(HMUI::ScrollView* view, UnityEngine::RectTransform* track, UnityEngine::RectTransform* handle) {
    scrollView = view;
    trackRect = track;
    handleRect = handle;
    if (!scrollView) return;
    // Keep the handle in sync when the user scrolls by THUMBSTICK (the scroll
    // view's own input path, untouched here). The delegate holds `this`, so it
    // shares the scroll view's lifetime — both are destroyed with the tab.
    auto self = this;
    scrollView->add_scrollPositionChangedEvent(
        custom_types::MakeDelegate<::System::Action_1<float_t>*>(
            (std::function<void(float_t)>)[self](float_t) {
                if (self) self->SyncHandle();
            }));
    SyncHandle();
}

void FeedScrollBar::ScrollToPointer(UnityEngine::EventSystems::PointerEventData* eventData) {
    if (!scrollView || !trackRect || !eventData) return;
    UnityEngine::Camera* cam = eventData->pressEventCamera ? eventData->pressEventCamera.ptr() : nullptr;
    UnityEngine::Vector2 local{};
    if (!UnityEngine::RectTransformUtility::ScreenPointToLocalPointInRectangle(
            trackRect, eventData->position, cam, byref(local)))
        return;
    auto rect = trackRect->get_rect();
    float height = rect.get_height();
    if (height <= 0.0f) return;
    // Normalize the pointer from the track's TOP (scroll position 0) down to
    // its bottom, independent of the track's pivot (rect is pivot-relative).
    float t = std::clamp((rect.get_yMax() - local.y) / height, 0.0f, 1.0f);
    scrollView->ScrollTo(t * scrollView->get_scrollableSize(), false);
    SyncHandle();
}

void FeedScrollBar::OnPointerDown(UnityEngine::EventSystems::PointerEventData* eventData) {
    ScrollToPointer(eventData);
}

void FeedScrollBar::OnDrag(UnityEngine::EventSystems::PointerEventData* eventData) {
    ScrollToPointer(eventData);
}

void FeedScrollBar::SyncHandle() {
    if (!scrollView || !trackRect || !handleRect) return;
    float trackH = trackRect->get_rect().get_height();
    if (trackH <= 0.0f) return;

    // Handle height proportional to how much of the content is visible.
    float content = scrollView->get_contentSize();
    float viewport = scrollView->get_scrollPageSize();
    float visibleFrac = (content > 0.0f) ? std::clamp(viewport / content, MIN_HANDLE_FRACTION, 1.0f) : 1.0f;
    float handleH = trackH * visibleFrac;
    // Handle is top-stretched horizontally (anchors {0,1}-{1,1}, pivot {0.5,1}),
    // so sizeDelta.x = 0 keeps it the track's width and sizeDelta.y sets height.
    auto size = handleRect->get_sizeDelta();
    handleRect->set_sizeDelta({size.x, handleH});

    // Position: 0 at top, travel = trackH - handleH downward.
    float scrollable = scrollView->get_scrollableSize();
    float frac = (scrollable > 0.0f) ? std::clamp(scrollView->get_position() / scrollable, 0.0f, 1.0f) : 0.0f;
    float travel = trackH - handleH;
    auto pos = handleRect->get_anchoredPosition();
    handleRect->set_anchoredPosition({pos.x, -frac * travel});
}
