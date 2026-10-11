interface FooterProps {
    variant: 'footer';
    title: string;
    subtitle: string;
}
interface BadgeProps {
    variant: 'badge';
    label: string;
}
type Props = FooterProps | BadgeProps;
export declare function ConnectionStatus(props: Props): import("react").JSX.Element;
export {};
